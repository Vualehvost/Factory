using System.Collections.Concurrent;

namespace RobotFactoryBot;

public record struct RobotSpec(string Color, bool Handles);

/// <summary>
/// Состояние отдельного прогона (изолировано по runId).
/// </summary>
public class RunSession
{
    public StartRequest Config { get; }
    public Dictionary<string, BuyerModel> Buyers { get; } = new();
    public List<RobotSpec> AllRobotSpecs { get; } = new();

    // Кэш для идемпотентности по seq
    private int _lastSeq = -1;
    private EventResponse _lastResponse = new();
    private readonly object _lock = new();

    public RunSession(StartRequest config)
    {
        Config = config;

        var colors = new List<string> { "red", "green", "blue" };
        if (config.Attributes.TryGetValue("color", out var colorElems) && colorElems.Count > 0)
        {
            colors = colorElems.Select(e => e.GetString() ?? "red").ToList();
        }

        var handlesList = new List<bool> { false, true };
        if (config.Attributes.TryGetValue("handles", out var handleElems) && handleElems.Count > 0)
        {
            handlesList = handleElems.Select(e => e.GetBoolean()).ToList();
        }

        foreach (var c in colors)
        {
            foreach (var h in handlesList)
            {
                AllRobotSpecs.Add(new RobotSpec(c, h));
            }
        }

        foreach (var buyerId in config.Buyers)
        {
            Buyers[buyerId] = new BuyerModel(buyerId, AllRobotSpecs, config.PriceCeiling);
        }
    }

    public EventResponse ProcessEvent(EventRequest req)
    {
        lock (_lock)
        {
            if (req.Seq == _lastSeq)
            {
                return _lastResponse;
            }

            var response = FactoryEngine.Decide(this, req);
            _lastSeq = req.Seq;
            _lastResponse = response;
            return response;
        }
    }
}

/// <summary>
/// Модель знаний о покупателе: объединяет матрицу ценовых вилок (low..high)
/// и дедуктивный фильтр гипотез идеального образца (Pmax, f).
/// </summary>
public class BuyerModel
{
    public string BuyerId { get; }
    private readonly Dictionary<RobotSpec, PriceRange> _ranges = new();
    private readonly List<SampleHypothesis> _hypotheses = new();
    private readonly double _priceCeiling;
    private readonly Dictionary<RobotSpec, double> _lastOfferedPrice = new();

    public class PriceRange
    {
        public double Low { get; set; }
        public double High { get; set; }
        public bool Confirmed { get; set; }
        public double? LastSoldPrice { get; set; }
    }

    private class SampleHypothesis
    {
        public RobotSpec Target { get; init; }
        public double PmaxLow { get; set; } = 45.0;
        public double PmaxHigh { get; set; } = 75.0;
        public double FLow { get; set; } = 0.30;
        public double FHigh { get; set; } = 0.60;
        public bool IsPossible { get; set; } = true;
    }

    public BuyerModel(string buyerId, List<RobotSpec> allSpecs, double priceCeiling)
    {
        BuyerId = buyerId;
        _priceCeiling = priceCeiling;

        foreach (var spec in allSpecs)
        {
            // Максимально возможная цена Pmax в v1 = 75 (ограничена priceCeiling)
            _ranges[spec] = new PriceRange
            {
                Low = 0.0,
                High = Math.Min(75.0, priceCeiling),
                Confirmed = false
            };

            _hypotheses.Add(new SampleHypothesis
            {
                Target = spec,
                PmaxLow = 45.0,
                PmaxHigh = Math.Min(75.0, priceCeiling)
            });
        }
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
            // Учитываем поправку +-2%: базовая цена лежит в [p / 1.02, p / 0.98]
            double baseLow = p / 1.02;
            double baseHigh = p / 0.98;
            range.Low = Math.Max(range.Low, baseLow * 0.98);
            range.High = Math.Min(range.High, baseHigh * 1.02);

            UpdateHypothesesOnSold(spec, p);
        }
        else if (outcome == "refused" && reason == "below_min_price")
        {
            // Мы знаем цену последнего предложения только через внешнюю запись,
            // но если она была сохранена при отправке offer — обновляем верхнюю границу.
            if (_lastOfferedPrice.TryGetValue(spec, out double attemptedMinPrice))
            {
                // Так как реальная цена (с учетом до -2% штрафа) оказалась < attemptedMinPrice,
                // базовая цена точно ниже attemptedMinPrice / 0.98.
                range.High = Math.Min(range.High, attemptedMinPrice);
                if (range.Low >= range.High)
                {
                    range.Low = Math.Max(0, range.High - 1.0);
                }

                UpdateHypothesesOnRefused(spec, attemptedMinPrice);
            }
        }
    }

    public void RecordOfferAttempt(RobotSpec spec, double minPrice)
    {
        _lastOfferedPrice[spec] = minPrice;
    }

    private static int Distance(RobotSpec a, RobotSpec b)
    {
        int d = 0;
        if (a.Color != b.Color) d++;
        if (a.Handles != b.Handles) d++;
        return d;
    }

    private void UpdateHypothesesOnSold(RobotSpec spec, double price)
    {
        foreach (var h in _hypotheses.Where(x => x.IsPossible))
        {
            int d = Distance(h.Target, spec);
            if (d == 2)
            {
                // При 2 расхождениях базовая цена строго 0
                if (price > 0.01) h.IsPossible = false;
            }
            else if (d == 0)
            {
                // Базовая цена = Pmax. С учетом +-2%: price / 1.02 <= Pmax <= price / 0.98
                double estLow = price / 1.02;
                double estHigh = price / 0.98;
                h.PmaxLow = Math.Max(h.PmaxLow, estLow);
                h.PmaxHigh = Math.Min(h.PmaxHigh, estHigh);
                if (h.PmaxLow > h.PmaxHigh + 0.5) h.IsPossible = false;
            }
            else if (d == 1)
            {
                // Базовая цена = Pmax * f. Диапазон Pmax*f in [45*0.30, 75*0.60] = [13.5, 45.0]
                double estBaseLow = price / 1.02;
                double estBaseHigh = price / 0.98;
                if (estBaseHigh < 13.0 || estBaseLow > 46.0)
                {
                    h.IsPossible = false;
                }
                else
                {
                    // Сужаем Pmax и f
                    h.PmaxLow = Math.Max(h.PmaxLow, estBaseLow / h.FHigh);
                    h.PmaxHigh = Math.Min(h.PmaxHigh, estBaseHigh / h.FLow);
                    h.FLow = Math.Max(h.FLow, estBaseLow / h.PmaxHigh);
                    h.FHigh = Math.Min(h.FHigh, estBaseHigh / h.PmaxLow);
                    if (h.PmaxLow > h.PmaxHigh + 0.5 || h.FLow > h.FHigh + 0.02)
                        h.IsPossible = false;
                }
            }
        }
        RecomputeRangesFromHypotheses();
    }

    private void UpdateHypothesesOnRefused(RobotSpec spec, double minPrice)
    {
        foreach (var h in _hypotheses.Where(x => x.IsPossible))
        {
            int d = Distance(h.Target, spec);
            if (d == 0)
            {
                // Pmax * 0.98 < minPrice => Pmax < minPrice / 0.98
                h.PmaxHigh = Math.Min(h.PmaxHigh, minPrice / 0.98);
                if (h.PmaxLow > h.PmaxHigh + 0.5) h.IsPossible = false;
            }
            else if (d == 1)
            {
                // Pmax * f * 0.98 < minPrice
                double maxProduct = minPrice / 0.98;
                if (h.PmaxLow * h.FLow > maxProduct + 0.5)
                {
                    h.IsPossible = false;
                }
                else
                {
                    h.PmaxHigh = Math.Min(h.PmaxHigh, maxProduct / h.FLow);
                    h.FHigh = Math.Min(h.FHigh, maxProduct / h.PmaxLow);
                }
            }
            // Если d == 2 и minPrice > 0, отказ ожидаем и не опровергает гипотезу
        }
        RecomputeRangesFromHypotheses();
    }

    private void RecomputeRangesFromHypotheses()
    {
        var active = _hypotheses.Where(x => x.IsPossible).ToList();
        if (active.Count == 0) return;

        foreach (var kvp in _ranges)
        {
            var spec = kvp.Key;
            var range = kvp.Value;
            if (range.Confirmed) continue;

            double minPossible = double.MaxValue;
            double maxPossible = 0.0;

            foreach (var h in active)
            {
                int d = Distance(h.Target, spec);
                if (d == 0)
                {
                    minPossible = Math.Min(minPossible, h.PmaxLow);
                    maxPossible = Math.Max(maxPossible, h.PmaxHigh);
                }
                else if (d == 1)
                {
                    minPossible = Math.Min(minPossible, h.PmaxLow * h.FLow);
                    maxPossible = Math.Max(maxPossible, h.PmaxHigh * h.FHigh);
                }
                else
                {
                    minPossible = Math.Min(minPossible, 0.0);
                    maxPossible = Math.Max(maxPossible, 0.0);
                }
            }

            if (minPossible <= maxPossible)
            {
                range.Low = Math.Max(range.Low, minPossible);
                range.High = Math.Min(range.High, maxPossible);
            }
        }
    }

    /// <summary>
    /// Возвращает оценку ожидаемой выручки и оптимальную цену minPrice для предложения.
    /// </summary>
    public (double ExpectedValue, double OfferMinPrice, double InfoGain) EvaluateOffer(RobotSpec spec, int ticksLeft)
    {
        var r = _ranges[spec];

        // Если цена уже подтверждена продажей — ставим цену чуть ниже известной (с учетом разброса -2%),
        // чтобы сделка прошла с 99% вероятностью без потери 8 тактов кулдауна.
        if (r.Confirmed && r.LastSoldPrice.HasValue)
        {
            double safePrice = Math.Floor(r.Low * 0.985);
            return (r.Low, Math.Max(1.0, safePrice), 0.0);
        }

        // Если верхняя граница ниже себестоимости сборки + комиссии (около 14), продавать за деньги невыгодно
        if (r.High < 14.0)
        {
            return (0.0, 0.0, 0.0);
        }

        // Бинарный поиск внутри актуальной вилки [Low, High]
        // Учитываем, что гипотеза d=0 дает минимум 45, а d=1 максимум 45.
        double effectiveLow = Math.Max(r.Low, 14.0);
        double effectiveHigh = r.High;

        double midPrice;
        if (effectiveHigh > 46.0 && effectiveLow < 44.0)
        {
            // Проверяем, является ли этот робот идеальным образцом (Pmax >= 45)
            midPrice = 44.5;
        }
        else
        {
            midPrice = Math.Floor((effectiveLow + effectiveHigh) / 2.0);
        }

        midPrice = Math.Clamp(midPrice, 14.0, _priceCeiling);

        // Бонус за разведку снижается к концу смены
        double uncertainty = Math.Max(0, r.High - r.Low);
        double infoBonus = ticksLeft > 40 ? uncertainty * 0.35 : 0.0;
        double expected = (effectiveLow + effectiveHigh) * 0.5 + infoBonus;

        return (expected, midPrice, infoBonus);
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

/// <summary>
/// Главный планировщик завода.
/// </summary>
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
            // На случай, если сервис перезапустился посреди теста — восстанавливаем базовую сессию
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

    public static void FinishRun(string runId)
    {
        Sessions.TryRemove(runId, out _);
    }

    public static DebugResponse? GetDebug(string runId)
    {
        if (!Sessions.TryGetValue(runId, out var session)) return null;
        var beliefs = session.Buyers.Values.SelectMany(b => b.GetDebugBeliefs()).ToList();
        return new DebugResponse
        {
            Beliefs = beliefs,
            Note = "Bayesian hypothesis elimination + binary price search"
        };
    }

    public static EventResponse Decide(RunSession session, EventRequest req)
    {
        var commands = new List<FactoryCommand>();
        var state = req.State;
        int t = req.T;
        int ticksLeft = Math.Max(0, session.Config.Horizon - t);

        // 1. Обновляем знания о рынке, если пришло событие sale_result
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

        // Виртуальное состояние текущего такта (чтобы не дублировать предметы и станки)
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

        // 2. Обработка входящей поставки (supply)
        if (req.Event.Type == "supply" && req.Event.Item != null && freeStorage > 0)
        {
            var item = req.Event.Item;
            double price = req.Event.Price ?? 0;

            if (ShouldAcceptSupply(item, price, availableItems, freeStorage, ticksLeft))
            {
                commands.Add(FactoryCommand.Accept(item.Id));
                availableItems.Add(item);
                freeStorage--;
            }
        }

        // 3. Продажа готовых роботов свободным покупателям
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

                var (expectedVal, minPrice, _) = buyer.EvaluateOffer(spec, ticksLeft);
                if (expectedVal > 14.0 && expectedVal > bestScore)
                {
                    bestScore = expectedVal;
                    bestBuyer = buyer.BuyerId;
                    bestMinPrice = minPrice;
                }
            }

            if (bestBuyer != null)
            {
                commands.Add(FactoryCommand.Offer(bestBuyer, robot.Id, bestMinPrice));
                session.Buyers[bestBuyer].RecordOfferAttempt(spec, bestMinPrice);
                busyBuyers.Add(bestBuyer);
                availableItems.Remove(robot);
            }
            else if (ticksLeft < 10 || (freeStorage <= 1 && AllBuyersRejectSpec(session, spec)))
            {
                // Если робот гарантированно не нужен ни одному покупателю за разумные деньги, а место кончается
                commands.Add(FactoryCommand.Discard(robot.Id));
                availableItems.Remove(robot);
                freeStorage++;
            }
        }

        // Определяем наиболее востребованные спецификации роботов на текущий момент
        var targetSpecs = RankDesiredSpecs(session, availableItems, ticksLeft);

        // 4. Запуск станков (в порядке приоритета от конца цепочки к началу, чтобы освобождать склад)

        // 4.1. Сборка (m-assemble): окрашенный body + control -> robot
        if (!busyMachines.Contains("m-assemble") && ticksLeft >= 4)
        {
            var paintedBodies = availableItems
                .Where(i => i.Kind == "body" && i.GetColor() != null)
                .OrderByDescending(b => GetSpecPriority(targetSpecs, new RobotSpec(b.GetColor()!, b.GetHandles() ?? false)))
                .ToList();
            var control = availableItems.FirstOrDefault(i => i.Kind == "control");

            if (paintedBodies.Count > 0 && control != null)
            {
                var body = paintedBodies[0];
                commands.Add(FactoryCommand.Start("m-assemble", "assemble", body.Id, control.Id));
                busyMachines.Add("m-assemble");
                availableItems.Remove(body);
                availableItems.Remove(control);
                // 2 предмета ушли со склада, 1 место зарезервировано под выход станка -> freeStorage += 1
                freeStorage++;
            }
        }

        // 4.2. Покраска (m-paint): неокрашенный body -> окрашенный body
        if (!busyMachines.Contains("m-paint") && ticksLeft >= 7)
        {
            var unpaintedBodies = availableItems
                .Where(i => i.Kind == "body" && i.GetColor() == null)
                .ToList();

            if (unpaintedBodies.Count > 0)
            {
                // Выбираем корпус и цвет, которые дадут самую востребованную комбинацию (color, handles)
                Item? bestBody = null;
                string bestColor = "red";
                double bestPriority = double.MinValue;

                foreach (var body in unpaintedBodies)
                {
                    bool hasHandles = body.GetHandles() ?? false;
                    foreach (var color in new[] { "red", "green", "blue" })
                    {
                        double prio = GetSpecPriority(targetSpecs, new RobotSpec(color, hasHandles));
                        if (prio > bestPriority)
                        {
                            bestPriority = prio;
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

        // 4.3. Установка ручек (m-handles): сырой body (color=null, handles=false) -> body (handles=true)
        if (!busyMachines.Contains("m-handles") && ticksLeft >= 9)
        {
            var rawBody = availableItems.FirstOrDefault(i =>
                i.Kind == "body" && i.GetColor() == null && (i.GetHandles() == false));

            if (rawBody != null)
            {
                // Сравниваем спрос на роботов с ручками и без ручек
                double demandWithHandles = targetSpecs.Where(x => x.Key.Handles).Sum(x => x.Value);
                double demandWithoutHandles = targetSpecs.Where(x => !x.Key.Handles).Sum(x => x.Value);

                int existingWithHandles = availableItems.Count(i => i.Kind == "body" && i.GetHandles() == true);
                int existingWithoutHandles = availableItems.Count(i => i.Kind == "body" && i.GetHandles() == false);

                // Делаем ручки, если спрос на них выше или если без ручек уже накопился запас
                bool shouldAddHandles = (demandWithHandles >= demandWithoutHandles * 0.85 && existingWithHandles <= existingWithoutHandles)
                                        || existingWithoutHandles >= 2;

                if (shouldAddHandles)
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

            // Не режем слишком много корпусов впрок, чтобы не платить повышенную аренду склада (0.2 против 0.1)
            if (sheet != null && currentBodies < 4)
            {
                commands.Add(FactoryCommand.Start("m-cut", "cut", sheet.Id));
                busyMachines.Add("m-cut");
                availableItems.Remove(sheet);
            }
        }

        // 5. Очистка склада при критическом переполнении (discard)
        if (freeStorage <= 1)
        {
            int sheets = availableItems.Count(i => i.Kind == "sheet");
            int controls = availableItems.Count(i => i.Kind == "control");

            if (sheets > controls + 2)
            {
                var extraSheet = availableItems.FirstOrDefault(i => i.Kind == "sheet");
                if (extraSheet != null)
                {
                    commands.Add(FactoryCommand.Discard(extraSheet.Id));
                    availableItems.Remove(extraSheet);
                    freeStorage++;
                }
            }
            else if (controls > sheets + 2)
            {
                var extraControl = availableItems.FirstOrDefault(i => i.Kind == "control");
                if (extraControl != null)
                {
                    commands.Add(FactoryCommand.Discard(extraControl.Id));
                    availableItems.Remove(extraControl);
                    freeStorage++;
                }
            }
        }

        return new EventResponse { Commands = commands };
    }

    private static bool ShouldAcceptSupply(Item item, double price, List<Item> inventory, int freeStorage, int ticksLeft)
    {
        // В конце смены не закупаем сырье, которое не успеем переработать и продать (минимум 11 тактов на цикл)
        if (ticksLeft < 11) return false;

        int sheetsAndBodies = inventory.Count(i => i.Kind == "sheet" || i.Kind == "body");
        int controls = inventory.Count(i => i.Kind == "control");

        if (item.Kind == "sheet")
        {
            // Не берем листы, если их суммарно с корпусами уже слишком много или склад почти забит
            if (sheetsAndBodies >= 5) return false;
            if (freeStorage <= 2 && sheetsAndBodies > controls) return false;
            return price <= 6.0;
        }

        if (item.Kind == "control")
        {
            if (controls >= 5) return false;
            if (freeStorage <= 2 && controls > sheetsAndBodies) return false;
            return price <= 5.0;
        }

        return freeStorage > 2;
    }

    private static Dictionary<RobotSpec, double> RankDesiredSpecs(RunSession session, List<Item> inventory, int ticksLeft)
    {
        var scores = new Dictionary<RobotSpec, double>();

        foreach (var spec in session.AllRobotSpecs)
        {
            // Находим максимальную ожидаемую цену среди всех покупателей
            double bestBuyerVal = 0;
            foreach (var buyer in session.Buyers.Values)
            {
                var (val, _, _) = buyer.EvaluateOffer(spec, ticksLeft);
                if (val > bestBuyerVal) bestBuyerVal = val;
            }

            // Штрафуем за уже имеющихся на складе роботов или окрашенные корпуса такого же типа,
            // чтобы не производить дубликаты под одного покупателя (у него кулдаун 8 тактов)
            int alreadyInPipeline = inventory.Count(i =>
                (i.Kind == "robot" || (i.Kind == "body" && i.GetColor() != null)) &&
                i.GetColor() == spec.Color &&
                (i.GetHandles() ?? false) == spec.Handles);

            scores[spec] = bestBuyerVal - alreadyInPipeline * 25.0;
        }

        return scores;
    }

    private static double GetSpecPriority(Dictionary<RobotSpec, double> scores, RobotSpec spec)
    {
        return scores.TryGetValue(spec, out var val) ? val : 0.0;
    }

    private static bool AllBuyersRejectSpec(RunSession session, RobotSpec spec)
    {
        return session.Buyers.Values.All(b => b.EvaluateOffer(spec, 50).ExpectedValue < 12.0);
    }
}