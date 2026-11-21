using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

namespace RobotFactoryBot;

public record struct RobotSpec(string Color, bool Handles);

public class RunSession
{
    public StartRequest Config { get; }
    public Dictionary<string, BuyerModel> Buyers { get; } = new();
    public List<RobotSpec> AllRobotSpecs { get; } = new();
    public Dictionary<string, int> ItemArrivalTick { get; } = new();

    private int _lastSeq = -1;
    private EventResponse _lastResponse = new();
    private readonly object _lock = new();

    public RunSession(StartRequest config)
    {
        Config = config;

        var colors = new List<string> { "red", "green", "blue" };
        if (config.Attributes.TryGetValue("color", out var colorElems) && colorElems.Count > 0)
            colors = colorElems.Select(e => e.GetString() ?? "red").ToList();

        var handlesList = new List<bool> { false, true };
        if (config.Attributes.TryGetValue("handles", out var handleElems) && handleElems.Count > 0)
            handlesList = handleElems.Select(e => e.GetBoolean()).ToList();

        foreach (var c in colors)
            foreach (var h in handlesList)
                AllRobotSpecs.Add(new RobotSpec(c, h));

        foreach (var buyerId in config.Buyers)
            Buyers[buyerId] = new BuyerModel(buyerId, AllRobotSpecs, config.PriceCeiling);
    }

    public EventResponse ProcessEvent(EventRequest req)
    {
        lock (_lock)
        {
            if (req.Seq == _lastSeq)
                return _lastResponse;

            var response = FactoryEngine.Decide(this, req);
            _lastSeq = req.Seq;
            _lastResponse = response;
            return response;
        }
    }
}

public class BuyerModel
{
    public string BuyerId { get; }
    private readonly Dictionary<RobotSpec, PriceRange> _ranges = new();
    private readonly Dictionary<RobotSpec, double> _lastOfferedPrice = new();
    private readonly double _priceCeiling;

    public class PriceRange
    {
        public double Low { get; set; }
        public double High { get; set; }
        public bool Confirmed { get; set; }
        public int Refusals { get; set; }
        public double? LastSoldPrice { get; set; }
    }

    public BuyerModel(string buyerId, List<RobotSpec> allSpecs, double priceCeiling)
    {
        BuyerId = buyerId;
        _priceCeiling = priceCeiling;

        foreach (var spec in allSpecs)
        {
            _ranges[spec] = new PriceRange
            {
                Low = 0.0,
                High = Math.Min(75.0, priceCeiling),
                Confirmed = false
            };
        }
    }

    public void RecordOfferAttempt(RobotSpec spec, double minPrice)
    {
        _lastOfferedPrice[spec] = minPrice;
    }

    public void UpdateFromSaleResult(RobotSpec spec, string outcome, double? soldPrice, string? reason)
    {
        if (reason == "cooldown") return;
        if (!_ranges.TryGetValue(spec, out var range)) return;

        if (outcome == "sold" && soldPrice.HasValue)
        {
            double p = soldPrice.Value;
            range.LastSoldPrice = p;
            range.Confirmed = true;
            range.Refusals = 0;

            // С учетом +-2% разброса оцениваем коридор базовой цены
            double estBase = p;
            range.Low = Math.Max(range.Low, estBase * 0.96);
            range.High = Math.Min(range.High, estBase * 1.04);

            // Логический вывод для других типов этого же покупателя:
            // Если цена >= 43, значит этот spec почти наверняка идеальный образец (d=0, Pmax >= 45).
            // Тогда любой робот с d=2 (другой цвет И другие ручки) стоит строго 0!
            if (p >= 43.0)
            {
                foreach (var other in _ranges.Keys.ToList())
                {
                    int dist = Distance(spec, other);
                    if (dist == 2)
                    {
                        _ranges[other].Low = 0;
                        _ranges[other].High = 0;
                        _ranges[other].Confirmed = true;
                    }
                    else if (dist == 1 && !_ranges[other].Confirmed)
                    {
                        // При d=1 цена = Pmax * f, где f in [0.30, 0.60]
                        _ranges[other].Low = Math.Max(_ranges[other].Low, p * 0.29);
                        _ranges[other].High = Math.Min(_ranges[other].High, p * 0.62);
                    }
                }
            }
        }
        else if (outcome == "refused" && reason == "below_min_price")
        {
            range.Refusals++;
            if (_lastOfferedPrice.TryGetValue(spec, out double attempted))
            {
                range.High = Math.Min(range.High, attempted - 0.5);

                // Если цена была > 28 и нам отказали, значит это точно не d=0 (ведь Pmax >= 45 -> min 44.1),
                // а при d=1 максимум возможного Pmax*f = 75 * 0.60 = 45.
                // Если отказали при цене <= 13.0, значит цена равна 0 (это d=2)!
                if (attempted <= 13.5)
                {
                    range.Low = 0;
                    range.High = 0;
                    range.Confirmed = true;
                }
                else if (range.Low >= range.High)
                {
                    range.Low = Math.Max(0, range.High - 3.0);
                }
            }
        }
    }

    private static int Distance(RobotSpec a, RobotSpec b)
    {
        int d = 0;
        if (a.Color != b.Color) d++;
        if (a.Handles != b.Handles) d++;
        return d;
    }

    /// <summary>
    /// Возвращает (Ожидаемая выручка, minPrice для offer, Вероятность успеха).
    /// </summary>
    public (double ExpectedValue, double OfferMinPrice, double WinProb) EvaluateOffer(RobotSpec spec, int ticksLeft)
    {
        var r = _ranges[spec];

        // Если знаем точную цену — продаём с запасом 2.5% вниз, чтобы не нарваться на случайный минус
        if (r.Confirmed)
        {
            if (r.High < 13.5) return (0.0, 0.0, 0.0);
            double safePrice = Math.Floor((r.LastSoldPrice ?? r.Low) * 0.975);
            return (r.LastSoldPrice ?? r.Low, Math.Max(13.0, safePrice), 0.98);
        }

        // Если верхняя граница уже упала ниже себестоимости (13.5), продавать нет смысла
        if (r.High < 13.5)
        {
            return (0.0, 0.0, 0.0);
        }

        double effLow = Math.Max(r.Low, 13.0);
        double effHigh = r.High;
        double offerPrice;

        // Первый пробный выстрел по неизвестному типу:
        // Ставим 42.5 — это ниже минимального Pmax (45 * 0.98 = 44.1), но выше среднего Pmax*f (~27).
        // Так мы за 1 шаг выясняем, идеальный ли это робот для покупателя!
        if (effHigh > 45.0 && effLow < 42.0 && r.Refusals == 0)
        {
            offerPrice = 42.5;
        }
        else
        {
            // Классическое деление пополам
            offerPrice = Math.Floor((effLow + effHigh) * 0.5);
        }

        offerPrice = Math.Clamp(offerPrice, 13.0, _priceCeiling);

        // Оценка ценности предложения (ожидаемая цена + ценность разведки)
        double midEstimate = (effLow + effHigh) * 0.5;
        double explorationBonus = (ticksLeft > 50 && r.Refusals < 3) ? (effHigh - effLow) * 0.25 : 0.0;
        double score = midEstimate + explorationBonus;

        return (score, offerPrice, 0.5);
    }

    public double GetBestKnownOrPotentialPrice(RobotSpec spec)
    {
        var r = _ranges[spec];
        if (r.Confirmed) return r.High < 13.5 ? 0.0 : (r.LastSoldPrice ?? r.Low);
        if (r.High < 13.5) return 0.0;
        return (Math.Max(r.Low, 13.0) + r.High) * 0.5;
    }

    public IEnumerable<DebugBelief> GetDebugBeliefs()
    {
        foreach (var kvp in _ranges)
        {
            yield return new DebugBelief
            {
                BuyerId = BuyerId,
                Color = kvp.Key.Color,
                Handles = kvp.Key.Handles,
                Low = Math.Round(kvp.Value.Low, 2),
                High = Math.Round(kvp.Value.High, 2)
            };
        }
    }
}

public static class FactoryEngine
{
    private static readonly ConcurrentDictionary<string, RunSession> Sessions = new();

    public static void StartRun(StartRequest request)
    {
        Sessions[request.RunId] = new RunSession(request);
    }

    public static EventResponse HandleEvent(string runId, EventRequest request)
    {
        if (!Sessions.TryGetValue(runId, out var session))
        {
            session = new RunSession(new StartRequest
            {
                RunId = runId,
                Capacity = request.State.Storage.Capacity,
                Buyers = request.State.Buyers.Select(b => b.Id).ToList()
            });
            Sessions[runId] = session;
        }

        return session.ProcessEvent(request);
    }

    public static void FinishRun(string runId) => Sessions.TryRemove(runId, out _);

    public static DebugResponse? GetDebug(string runId)
    {
        if (!Sessions.TryGetValue(runId, out var session)) return null;
        return new DebugResponse
        {
            Beliefs = session.Buyers.Values.SelectMany(b => b.GetDebugBeliefs()).ToList(),
            Note = "Direct range bisection + structural deduction"
        };
    }

    public static EventResponse Decide(RunSession session, EventRequest req)
    {
        var commands = new List<FactoryCommand>();
        var state = req.State;
        int t = req.T;
        int ticksLeft = Math.Max(0, session.Config.Horizon - t);

        // Запоминаем время появления предметов на складе, чтобы отслеживать залежавшиеся
        foreach (var item in state.Storage.Items)
        {
            session.ItemArrivalTick.TryAdd(item.Id, t);
        }

        // 1. Обновление рынка по результатам сделки
        if (req.Event.Type == "sale_result" && req.Event.BuyerId != null && req.Event.Item != null)
        {
            var color = req.Event.Item.GetColor();
            var handles = req.Event.Item.GetHandles();
            if (color != null && handles.HasValue && session.Buyers.TryGetValue(req.Event.BuyerId, out var buyerModel))
            {
                buyerModel.UpdateFromSaleResult(
                    new RobotSpec(color, handles.Value),
                    req.Event.Outcome ?? "refused",
                    req.Event.Price,
                    req.Event.Reason
                );
            }
        }

        var availableItems = state.Storage.Items.ToList();
        var busyMachines = state.Machines
            .Where(m => m.BusyUntil.HasValue && m.BusyUntil.Value > t)
            .Select(m => m.Id)
            .ToHashSet();
        var busyBuyers = state.Buyers
            .Where(b => b.CooldownUntil.HasValue && b.CooldownUntil.Value > t)
            .Select(b => b.Id)
            .ToHashSet();
        int freeStorage = state.Storage.Free;

        // 2. Приём поставок (supply)
        if (req.Event.Type == "supply" && req.Event.Item != null && freeStorage > 0)
        {
            var item = req.Event.Item;
            double price = req.Event.Price ?? 0;

            if (ShouldAcceptSupply(item, price, availableItems, freeStorage, ticksLeft))
            {
                commands.Add(FactoryCommand.Accept(item.Id));
                availableItems.Add(item);
                session.ItemArrivalTick[item.Id] = t;
                freeStorage--;
            }
        }

        // 3. Продажа готовых роботов
        var robots = availableItems.Where(i => i.Kind == "robot").ToList();
        foreach (var robot in robots)
        {
            var color = robot.GetColor();
            var handles = robot.GetHandles();
            if (color == null || !handles.HasValue) continue;

            var spec = new RobotSpec(color, handles.Value);
            string? bestBuyer = null;
            double bestScore = double.MinValue;
            double bestMinPrice = 0;

            foreach (var buyer in session.Buyers.Values)
            {
                if (busyBuyers.Contains(buyer.BuyerId)) continue;

                var (score, minPrice, _) = buyer.EvaluateOffer(spec, ticksLeft);
                if (score > 13.5 && score > bestScore)
                {
                    bestScore = score;
                    bestBuyer = buyer.BuyerId;
                    bestMinPrice = minPrice;
                }
            }

            int age = t - session.ItemArrivalTick.GetValueOrDefault(robot.Id, t);

            if (bestBuyer != null)
            {
                commands.Add(FactoryCommand.Offer(bestBuyer, robot.Id, bestMinPrice));
                session.Buyers[bestBuyer].RecordOfferAttempt(spec, bestMinPrice);
                busyBuyers.Add(bestBuyer);
                availableItems.Remove(robot);
            }
            else if (ticksLeft < 8 || AllBuyersRejectSpec(session, spec) || (freeStorage <= 2 && age > 20))
            {
                // Утилизируем неликвидного робота, чтобы не платить 0.5/такт за хранение
                commands.Add(FactoryCommand.Discard(robot.Id));
                availableItems.Remove(robot);
                freeStorage++;
            }
        }

        // Оцениваем спрос на каждую из 6 моделей роботов
        var specDemand = CalculateSpecDemand(session, availableItems);

        // 4. Запуск станков

        // 4.1. Сборка (m-assemble): окрашенный body + control -> robot
        if (!busyMachines.Contains("m-assemble") && ticksLeft >= 4)
        {
            var paintedBodies = availableItems
                .Where(i => i.Kind == "body" && i.GetColor() != null)
                .OrderByDescending(b => specDemand.GetValueOrDefault(new RobotSpec(b.GetColor()!, b.GetHandles() ?? false), 0))
                .ToList();
            var control = availableItems.FirstOrDefault(i => i.Kind == "control");

            // Не собираем нового робота, если на складе уже пылятся 3 непроданных робота
            if (paintedBodies.Count > 0 && control != null && robots.Count < 3)
            {
                var body = paintedBodies[0];
                commands.Add(FactoryCommand.Start("m-assemble", "assemble", body.Id, control.Id));
                busyMachines.Add("m-assemble");
                availableItems.Remove(body);
                availableItems.Remove(control);
                freeStorage++;
            }
        }

        // 4.2. Покраска (m-paint)
        if (!busyMachines.Contains("m-paint") && ticksLeft >= 7)
        {
            var unpaintedBodies = availableItems
                .Where(i => i.Kind == "body" && i.GetColor() == null)
                .ToList();

            if (unpaintedBodies.Count > 0)
            {
                Item? bestBody = null;
                string bestColor = "red";
                double bestScore = double.MinValue;

                foreach (var body in unpaintedBodies)
                {
                    bool hasHandles = body.GetHandles() ?? false;
                    foreach (var color in new[] { "red", "green", "blue" })
                    {
                        double score = specDemand.GetValueOrDefault(new RobotSpec(color, hasHandles), 0);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestBody = body;
                            bestColor = color;
                        }
                    }
                }

                if (bestBody != null)
                {
                    commands.Add(FactoryCommand.Start("m-paint", $"paint-{bestColor}", bestBody.Id));
                    busyMachines.Add("m-paint");
                    availableItems.Remove(bestBody);
                }
            }
        }

        // 4.3. Установка ручек (m-handles): БЕЗ принудительного добавления!
        if (!busyMachines.Contains("m-handles") && ticksLeft >= 9)
        {
            var rawBody = availableItems.FirstOrDefault(i =>
                i.Kind == "body" && i.GetColor() == null && (i.GetHandles() == false));

            if (rawBody != null)
            {
                double maxWithHandles = specDemand.Where(x => x.Key.Handles).Select(x => x.Value).DefaultIfEmpty(0).Max();
                double maxWithoutHandles = specDemand.Where(x => !x.Key.Handles).Select(x => x.Value).DefaultIfEmpty(0).Max();

                int countWithHandles = availableItems.Count(i => i.Kind == "body" && i.GetHandles() == true);
                int countWithoutHandles = availableItems.Count(i => i.Kind == "body" && i.GetHandles() == false);

                // Ставим ручки ТОЛЬКО если лучшие модели с ручками приносят больше денег (или сравнимо, когда без ручек уже есть запас)
                if (maxWithHandles > 18.0 &&
                    (maxWithHandles > maxWithoutHandles + 2.0 ||
                     (maxWithHandles >= maxWithoutHandles - 3.0 && countWithHandles < countWithoutHandles)))
                {
                    commands.Add(FactoryCommand.Start("m-handles", "add-handles", rawBody.Id));
                    busyMachines.Add("m-handles");
                    availableItems.Remove(rawBody);
                }
            }
        }

        // 4.4. Резка (m-cut): sheet -> raw body
        if (!busyMachines.Contains("m-cut") && ticksLeft >= 11)
        {
            var sheet = availableItems.FirstOrDefault(i => i.Kind == "sheet");
            int currentBodies = availableItems.Count(i => i.Kind == "body");

            if (sheet != null && currentBodies < 3)
            {
                commands.Add(FactoryCommand.Start("m-cut", "cut", sheet.Id));
                busyMachines.Add("m-cut");
                availableItems.Remove(sheet);
            }
        }

        // 5. Аварийная очистка склада от перекоса в сырье
        if (freeStorage <= 1)
        {
            int sheets = availableItems.Count(i => i.Kind == "sheet");
            int controls = availableItems.Count(i => i.Kind == "control");

            if (sheets > controls + 1)
            {
                var item = availableItems.FirstOrDefault(i => i.Kind == "sheet");
                if (item != null) { commands.Add(FactoryCommand.Discard(item.Id)); freeStorage++; }
            }
            else if (controls > sheets + 1)
            {
                var item = availableItems.FirstOrDefault(i => i.Kind == "control");
                if (item != null) { commands.Add(FactoryCommand.Discard(item.Id)); freeStorage++; }
            }
        }

        return new EventResponse { Commands = commands };
    }

    private static bool ShouldAcceptSupply(Item item, double price, List<Item> inventory, int freeStorage, int ticksLeft)
    {
        if (ticksLeft < 11) return false;

        int sheetsAndBodies = inventory.Count(i => i.Kind == "sheet" || i.Kind == "body");
        int controls = inventory.Count(i => i.Kind == "control");
        int robots = inventory.Count(i => i.Kind == "robot");

        // Если на складе уже много непроданных роботов — ставим закупку на паузу
        if (robots >= 3 && freeStorage <= 4) return false;

        if (item.Kind == "sheet")
        {
            if (sheetsAndBodies >= 4) return false;
            if (freeStorage <= 2 && sheetsAndBodies > controls) return false;
            return price <= 6.0;
        }

        if (item.Kind == "control")
        {
            if (controls >= 4) return false;
            if (freeStorage <= 2 && controls > sheetsAndBodies) return false;
            return price <= 5.0;
        }

        return freeStorage > 2;
    }

    private static Dictionary<RobotSpec, double> CalculateSpecDemand(RunSession session, List<Item> inventory)
    {
        var scores = new Dictionary<RobotSpec, double>();

        foreach (var spec in session.AllRobotSpecs)
        {
            // Суммируем две лучшие оценки покупателей (учитывает случай, когда несколько покупателей хотят один тип)
            var prices = session.Buyers.Values
                .Select(b => b.GetBestKnownOrPotentialPrice(spec))
                .OrderByDescending(p => p)
                .ToList();

            double topPrice = prices.FirstOrDefault();
            double secondPrice = prices.Skip(1).FirstOrDefault();
            double demandScore = topPrice + secondPrice * 0.25;

            // Штрафуем за уже произведённых или покрашенных роботов этого типа на складе
            int inPipeline = inventory.Count(i =>
                (i.Kind == "robot" || (i.Kind == "body" && i.GetColor() != null)) &&
                i.GetColor() == spec.Color &&
                (i.GetHandles() ?? false) == spec.Handles);

            scores[spec] = demandScore - inPipeline * 30.0;
        }

        return scores;
    }

    private static bool AllBuyersRejectSpec(RunSession session, RobotSpec spec)
    {
        return session.Buyers.Values.All(b => b.GetBestKnownOrPotentialPrice(spec) < 13.5);
    }
}
