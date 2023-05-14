using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Web3;
using System.Diagnostics;
using System.Numerics;
using System.Reflection.Metadata;
using Nethereum.BlockchainProcessing.Processor;
using Nethereum.BlockchainProcessing;
using Nethereum.BlockchainProcessing.ProgressRepositories;
using Raven.Client.Documents;
using System.Collections.Concurrent;
using Nethereum.Hex.HexTypes;
using System.Globalization;
using System.Collections.Generic;
using Newtonsoft.Json;
using static System.Formats.Asn1.AsnWriter;
namespace MyNamespace
    class Program
    {
        public class ContractDetails
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Symbol { get; set; }
            public double decimals { get; set; }
            public DateTimeOffset creationTime { get; set; }
            public bool ctrVerifStatus { get; set; }
            public DateTimeOffset lastCtrVerifCheck { get; set; }
            public bool sellable { get; set; }
            public double sellTax { get; set; }
            public double buyTax { get; set; }
            public override string ToString()
            {
                return $"Id: {Id}\nName: {Name}\nSymbol: {Symbol}\ndecimals: {decimals}\ncreationTime: {creationTime}\nctrVerifStatus: {ctrVerifStatus}\nsellable: {sellable}\nsellTax: {sellTax}\nbuyTax: {buyTax}";
            }
        }
        public class HexDataSet
        {
            public HexBigInteger BlockNumber { get; set; }
            public HexBigInteger TransactionIndex { get; set; }
            public HexBigInteger LogIndex { get; set; }
        }
        public class Ohlc
        {
            public DateTimeOffset Date { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public double Volume { get; set; }
            public double percentage { get; set; }
            public HexDataSet lastSwap { get; set; }
            public int txCounter { get; set; }
        }
        public class TickData
        {
            public string Id { get; set; }
            public string token0 { get; set; }
            public string token1 { get; set; }
            public double reserve0 { get; set; }
            public double reserve1 { get; set; }
            public string factory { get; set; }
            public double res0Dec { get; set; }
            public double res1Dec { get; set; }
            public string ohlcString { get; set; }
            public LimitedList<Ohlc> ohlc { get; set; }
            public ConcurrentDictionary<DateTime, string> jsonData { get; set; }
            public TickData()
            {
                ohlc = new LimitedList<Ohlc>(2);
            }
        }
        public class LimitedList<T> : List<T>
        {
            private readonly int _limit;
            public LimitedList(int limit)
            {
                _limit = limit;
            }
            public new void Add(T item)
            {
                lock (this)
                {
                    base.Add(item);
                    while (Count > _limit)
                    {
                        RemoveAt(0);
                    }
                }
            }
        }
        public class LimitedSortedDictionary<TKey, TValue> : SortedDictionary<TKey, TValue>
        {
            private readonly int _limit;
            private readonly object _lock = new object();
            public LimitedSortedDictionary(int limit)
            {
                _limit = limit;
            }
            public new void Add(TKey key, TValue value)
            {
                lock (_lock)
                {
                    while (Count >= _limit)
                    {
                        TKey oldestKey = Keys.First();
                        Remove(oldestKey);
                    }
                    base.Add(key, value);
                }
            }
            public List<Tuple<TKey, TKey>> GetKeyGaps()
            {
                lock (_lock)
                {
                    var keyList = Keys.ToList();
                    var gaps = new List<Tuple<TKey, TKey>>();
                    for (int i = 1; i < keyList.Count; i++)
                    {
                        dynamic prevKey = keyList[i - 1];
                        dynamic currKey = keyList[i];
                        if (currKey - prevKey > 1)
                        {
                            gaps.Add(new Tuple<TKey, TKey>(prevKey + 1, currKey - 1));
                        }
                    }
                    return gaps;
                }
            }
        }
        static Web3 firstWeb3 = new Web3("Your API here");
        static Web3 secondWeb3 = new Web3("Your API here");
        static BlockchainCrawlingProcessor blockProcessor;
        static BlockchainProcessor logsProcessor;
        static InMemoryBlockchainProgressRepository blockProgressRepo = new InMemoryBlockchainProgressRepository(0);
        static InMemoryBlockchainProgressRepository logProgressRepo = new InMemoryBlockchainProgressRepository(0);
        static DocumentStore tdataStore1 = new DocumentStore
        {
            Database = "tradesData"
        };
        static DocumentStore ctrStore = new DocumentStore
        {
            Database = "ContractsData"
        };
        static ConcurrentBag<TickData> documents = new ConcurrentBag<TickData>();
        static LimitedSortedDictionary<int, DateTimeOffset> blockandTimeData = new LimitedSortedDictionary<int, DateTimeOffset>(5000);
        static List<string> lpPairs = new List<string> { "0x0e09fabb73bd3ade0a17ecc321fd13a19e81ce82", "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c", "0x55d398326f99059ff775485246999027b3197955", "0xe9e7CEA3DedcA5984780Bafc599bD69ADd087D56", "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d", "0x14016E85a25aeb13065688cAFB43044C2ef86784" };
        static List<string> stableCoins = new List<string> { "0x55d398326f99059fF775485246999027B3197955", "0xe9e7CEA3DedcA5984780Bafc599bD69ADd087D56", "0x8AC76a51cc950d9822D68b83fE1Ad97B32Cd580d", "0x14016E85a25aeb13065688cAFB43044C2ef86784" };
        static FilterLogBlockNumberTransactionIndexLogIndexComparer comparer = new FilterLogBlockNumberTransactionIndexLogIndexComparer();
        static ConcurrentDictionary<BigInteger, ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>>> swapEvents = new ConcurrentDictionary<BigInteger, ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>>>();
        static BlockingCollection<ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>>> messageQueue = new BlockingCollection<ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>>>();
        static void Main(string[] args)
        {
            Console.SetBufferSize(Console.BufferWidth, 32766);
            lpPairs = lpPairs.ConvertAll(d => Web3.ToChecksumAddress(d));
            stableCoins = stableCoins.ConvertAll(d => Web3.ToChecksumAddress(d));
            tdataStore1.Initialize();
            ctrStore.Initialize();
            Task.Factory.StartNew(async () => await ValidDataCollector(), TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Factory.StartNew(async () => await LatestblockGetter(), TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Factory.StartNew(async () => await LogCrawler(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(async () => await BlockCrawler(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(async () => await ProcessSwapsAsync(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(async () => await DbUpadteListner(), TaskCreationOptions.LongRunning);
            Console.ReadLine();
        }
        static async Task LatestblockGetter()
        {
            try
            {
                var latestBlockNumber = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
                logProgressRepo = new InMemoryBlockchainProgressRepository(latestBlockNumber);
                blockProgressRepo = new InMemoryBlockchainProgressRepository(latestBlockNumber);
                Console.WriteLine("Starting block is " + blockProgressRepo.LastBlockProcessed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        public static async Task BlockCrawler()
        {
            try
            {
                if (blockProgressRepo.LastBlockProcessed.Value != BigInteger.One && blockProgressRepo.LastBlockProcessed.Value != BigInteger.Zero && blockProgressRepo.LastBlockProcessed.Value > 0)
                {
                    blockProcessor = firstWeb3.Processing.Blocks.CreateBlockProcessor(stepsConfiguration: steps =>
                    {
                        steps.TransactionStep.SetMatchCriteria((tx) => false);
                        steps.BlockStep.AddSynchronousProcessorHandler(async b =>
                        {
                            var utcTimestamp = DateTimeOffset.FromUnixTimeSeconds((long)b.Timestamp.Value);
                            await Console.Out.WriteLineAsync(b.Number.Value + " " + DateTimeOffset.FromUnixTimeSeconds((long)b.Timestamp.Value) + " Late by " + (DateTime.UtcNow - utcTimestamp).TotalSeconds + " pending " + messageQueue.Count);
                            blockandTimeData.TryAdd((int)b.Number.Value, utcTimestamp);
                        });
                    },
                    blockProgressRepository: blockProgressRepo, minimumBlockConfirmations: 1);
                    var cancellationToken = new CancellationToken();
                    blockProcessor.Orchestrator.BlockCrawlerStep.Enabled = true;
                    blockProcessor.Orchestrator.TransactionWithBlockCrawlerStep.Enabled = true;
                    blockProcessor.Orchestrator.TransactionWithReceiptCrawlerStep.Enabled = false;
                    blockProcessor.Orchestrator.ContractCreatedCrawlerStep.Enabled = false;
                    blockProcessor.Orchestrator.FilterLogCrawlerStep.Enabled = false;
                    await blockProcessor.ExecuteAsync(
                       cancellationToken: cancellationToken,
                       startAtBlockNumberIfNotProcessed: blockProgressRepo.LastBlockProcessed);
                }
                else
                {
                    await Task.Delay(5000);
                    await BlockCrawler();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred in blocks: {ex.Message}");
                await Task.Delay(5000);
                await BlockCrawler();
            }
            finally
            {
                Console.WriteLine($"Last block processed: {blockProgressRepo.LastBlockProcessed}");
                await BlockCrawler();
            }
        }
        public static async Task LogCrawler()
        {
            try
            {
                if (logProgressRepo.LastBlockProcessed.Value != BigInteger.One && logProgressRepo.LastBlockProcessed.Value != BigInteger.Zero && logProgressRepo.LastBlockProcessed.Value > 0)
                {
                    
                    logsProcessor = secondWeb3.Processing.Logs.CreateProcessor<SwapEventDTO>(async tfr =>
                    {
                        if (swapEvents.TryAdd(tfr.Log.BlockNumber.Value, new ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>>()))
                        {
                            swapEvents[tfr.Log.BlockNumber.Value].AddOrUpdate(tfr.Log.Address, new List<EventLog<SwapEventDTO>> { tfr }, (key, value) => { value.Add(tfr); return value; });
                            if (swapEvents.TryRemove(tfr.Log.BlockNumber.Value - 1, out var itemList))
                            {
                                await Console.Out.WriteLineAsync("added key for processing " + tfr.Log.BlockNumber.Value);
                                messageQueue.Add(itemList);
                            }
                            else
                            {
                                await Console.Out.WriteLineAsync("failed to remove key for processing  " + (tfr.Log.BlockNumber.Value - 1));
                            }
                        }
                        else
                        {
                            swapEvents[tfr.Log.BlockNumber.Value].AddOrUpdate(tfr.Log.Address, new List<EventLog<SwapEventDTO>> { tfr }, (key, value) => { value.Add(tfr); return value; });
                        }
                    }, blockProgressRepository: logProgressRepo, minimumBlockConfirmations: 5);
                    var cancellationToken2 = new CancellationToken();
                    await logsProcessor.ExecuteAsync(
                        cancellationToken: cancellationToken2,
                        startAtBlockNumberIfNotProcessed: logProgressRepo.LastBlockProcessed);
                }
                else
                {
                    await Task.Delay(5000);
                    await LogCrawler();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred in logs: {ex.Message}");
                await Task.Delay(5000);
                await LogCrawler();
            }
            finally
            {
                Console.WriteLine($"Last log  processed: {logProgressRepo.LastBlockProcessed}");
                await LogCrawler();
            }
        }
		public static async Task ProcessSwapsAsync()
        {
            try
            {
                while (true)
                {
                    foreach (var itemList in messageQueue.GetConsumingEnumerable())
                    {
                        await StoreSwaps(itemList);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception occurred in saver: {ex.Message}");
                await ProcessSwapsAsync();
            }
            finally
            {
                Console.WriteLine("Restarting");
                await ProcessSwapsAsync();
            }
        }
        static async Task ValidDataCollector()
        {
            try
            {
                using (var session = tdataStore1.OpenAsyncSession())
                {
                    var tempList = await session.Query<TickData>()
                           .Where(x => x.factory == "0xcA143Ce32Fe78f1f7019d7d551a6402fC5350c73")
                           .Select(x => new TickData
                           {
                               Id = x.Id,
                               token0 = x.token0,
                               token1 = x.token1,
                               reserve0 = x.reserve0,
                               reserve1 = x.reserve1,
                               res0Dec = x.res0Dec,
                               res1Dec = x.res1Dec
                           }).ToListAsync();
                    documents = new ConcurrentBag<TickData>(tempList.ToList());
                    Console.WriteLine("Count " + documents.Count);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        static async Task DbUpadteListner()
        {
            tdataStore1.Changes().ForAllDocuments().Subscribe(async change =>
            {
                if (!documents.Any(x => x.Id == change.Id))
                {
                    using (var session = tdataStore1.OpenAsyncSession())
                    {
                        try
                        {
                            var tdt = await session.LoadAsync<TickData>((change.Id));
                            documents.Add(tdt);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.ToString());
                        }
                    } 
                }
            });
            ctrStore.Changes().ForAllOperations().Subscribe( change => {
                Console.WriteLine("Operation #" + change.OperationId);
            });
        }
        static async Task DbCleaner()
        {
            try
            {
                using (var session2 = tdataStore1.OpenSession())
                {
                    var data = session2.Query<TickData>().ToList();
                    {
                        for (int i = 0; i < data.Count; i++)
                        {
                            data[i].jsonData = new ConcurrentDictionary<DateTime, string>();
                            data[i].ohlc = new LimitedList<Ohlc>(2);
                            session2.Store(data[i]);
                        }
                        session2.SaveChanges();
                    }
                    Console.WriteLine("Done ");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        public (double price, double quantity) GetPriceAndQuantity(double amount1Out, double amount0In, double amount1In, double amount0Out)
        {
            if (amount0In > 0 && amount1Out > 0)
            {
                return (amount1Out / amount0In, amount1Out);
            }
            else 
            {
                if (amount1In > 0 && amount0Out > 0)
                {
                    return (amount1In / amount0Out, amount1In);
                }
                else
                {
                    return (0, 0);
                }
            }
            
        }
        public static async Task StoreSwaps(ConcurrentDictionary<string, List<EventLog<SwapEventDTO>>> swaps)
        {
           
            try
            {
                await Console.Out.WriteLineAsync("removed key for processing " + swaps.FirstOrDefault().Value.First().Log.BlockNumber.Value + " remaining " + swaps.Count());
                foreach (var item in swaps)
                {
                    if (documents.Any(x => x.Id == Web3.ToChecksumAddress(item.Key)))
                    {
                        using (var session = tdataStore1.OpenAsyncSession())
                        {
                            var tdt = await session.LoadAsync<TickData>(Web3.ToChecksumAddress(item.Key));
                            foreach (var decoded in item.Value)
                            {
                                Console.WriteLine("Procesing " + decoded.Log.TransactionHash + " " + decoded.Log.BlockNumber.Value);
                                var doc = documents.FirstOrDefault(v => v.Id == decoded.Log.Address);
                                var amount0Out = (double)decoded.Event.Amount0Out / Math.Pow(10, tdt.res0Dec);
                                var amount1In = (double)decoded.Event.Amount1In / Math.Pow(10, tdt.res1Dec);
                                var amount0In = (double)decoded.Event.Amount0In / Math.Pow(10, tdt.res0Dec);
                                var amount1Out = (double)decoded.Event.Amount1Out / Math.Pow(10, tdt.res1Dec);
                                double price = 0;
                                double quantity = 0;
                                Program calculator = new Program();
                                if (!lpPairs.Contains(Web3.ToChecksumAddress(tdt.token0)) && lpPairs.Contains(Web3.ToChecksumAddress(tdt.token1)))
                                {
                                    (price, quantity) = calculator.GetPriceAndQuantity(amount1Out, amount0In, amount1In, amount0Out);
                                }
                                else if (lpPairs.Contains(Web3.ToChecksumAddress(tdt.token0)) && !lpPairs.Contains(Web3.ToChecksumAddress(tdt.token1)))
                                {
                                    (price, quantity) = calculator.GetPriceAndQuantity(amount0In, amount1Out, amount0Out, amount1In);
                                }
                                else if (lpPairs.Contains(Web3.ToChecksumAddress(tdt.token0)) && lpPairs.Contains(Web3.ToChecksumAddress(tdt.token1)))
                                {
                                    int stableCoinIndex = stableCoins.Select(sc => sc == Web3.ToChecksumAddress(tdt.token0) ? 0 : sc == Web3.ToChecksumAddress(tdt.token1) ? 1 : -1).DefaultIfEmpty(-1).FirstOrDefault();
                                    if (stableCoinIndex == 1)
                                    {
                                        (price, quantity) = calculator.GetPriceAndQuantity(amount1Out, amount0In, amount1In, amount0Out);
                                    }
                                    else if (stableCoinIndex == 0)
                                    {
                                        (price, quantity) = calculator.GetPriceAndQuantity(amount0In, amount1Out , amount0Out, amount1In);
                                    }
                                }
                                int waitCount = 0;
                                if (!blockandTimeData.ContainsKey((int)decoded.Log.BlockNumber.Value))
                                {
                                    var task = Task.Run(async () =>
                                    {
                                        while (!blockandTimeData.ContainsKey((int)decoded.Log.BlockNumber.Value) && waitCount < 10)
                                        {
                                            await Task.Delay(1000);
                                            waitCount++;
                                            Console.WriteLine("Still waiting for " + decoded.Log.BlockNumber.Value.ToString());
                                        }
                                    });
                                    await task;
                                    if (waitCount >= 10)
                                    {
                                        var latestBlockNumber = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(decoded.Log.BlockNumber);
                                        var utcTimestamp = DateTimeOffset.FromUnixTimeSeconds((long)latestBlockNumber.Timestamp.Value);
                                        blockandTimeData.TryAdd((int)latestBlockNumber.Number.Value, utcTimestamp);
                                    }
                                }
                                if (price > 0)
                                {
                                    var existingOhlc = tdt.ohlc.FirstOrDefault(x => x.Date.ToString("yyyy-MM-dd HH:mm") == blockandTimeData[(int)decoded.Log.BlockNumber.Value].ToString("yyyy-MM-dd HH:mm"));
                                    if (existingOhlc == null)
                                    {
                                        tdt.ohlc.Add(new Ohlc
                                        {
                                            Date = DateTime.ParseExact(blockandTimeData[(int)decoded.Log.BlockNumber.Value].ToString("yyyy-MM-dd HH:mm"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                                            Open = price,
                                            Close = price,
                                            Low = price,
                                            High = price,
                                            Volume = quantity,
                                            percentage = 0,
                                            lastSwap = new HexDataSet
                                            {
                                                BlockNumber = decoded.Log.BlockNumber,
                                                TransactionIndex = decoded.Log.TransactionIndex,
                                                LogIndex = decoded.Log.LogIndex
                                            },
                                            txCounter = 1,
                                        });
                                        if (tdt.ohlc.Count > 1)
                                        {
                                            Ohlc currentOhlc = tdt.ohlc[tdt.ohlc.Count - 1];
                                            Ohlc lastOhlc = tdt.ohlc[tdt.ohlc.Count - 2];
                                            tdt.ohlc.Last().percentage = (currentOhlc.Close - lastOhlc.Close) / lastOhlc.Close * 100;
                                        }
                                        if (tdt.ohlc.Count > 1)
                                        {
                                            string newItemJson = $"{{\"Date\":\"{tdt.ohlc[0].Date}\",\"Open\":{tdt.ohlc[0].Open},\"High\":{tdt.ohlc[0].High},\"Low\":{tdt.ohlc[0].Low},\"Close\":{tdt.ohlc[0].Close},\"Volume\":{tdt.ohlc[0].Volume},\"percentage\":{tdt.ohlc[0].percentage},\"txCounter\":{tdt.ohlc[0].txCounter}}}";
                                            tdt.jsonData.AddOrUpdate(blockandTimeData[(int)decoded.Log.BlockNumber.Value].Date, newItemJson, (key, oldValue) => oldValue + "," + newItemJson);
                                        }
                                    }
                                    else
                                    {
                                        if (existingOhlc.lastSwap != null)
                                        {
                                            if (comparer.Compare(existingOhlc.lastSwap, new HexDataSet { BlockNumber = decoded.Log.BlockNumber, TransactionIndex = decoded.Log.TransactionIndex, LogIndex = decoded.Log.LogIndex }) < 0)
                                            {
                                                existingOhlc.Close = price;
                                                existingOhlc.Low = Math.Min(existingOhlc.Low, price);
                                                existingOhlc.High = Math.Max(existingOhlc.High, price);
                                                existingOhlc.Volume += quantity;
                                                existingOhlc.lastSwap = new HexDataSet
                                                {
                                                    BlockNumber = decoded.Log.BlockNumber,
                                                    TransactionIndex = decoded.Log.TransactionIndex,
                                                    LogIndex = decoded.Log.LogIndex
                                                };
                                                if (tdt.ohlc.Count > 1)
                                                {
                                                    Ohlc currentOhlc = tdt.ohlc[tdt.ohlc.Count - 1];
                                                    Ohlc lastOhlc = tdt.ohlc[tdt.ohlc.Count - 2];
                                                    tdt.ohlc.Last().percentage = (currentOhlc.Close - lastOhlc.Close) / lastOhlc.Close * 100;
                                                }
                                                existingOhlc.txCounter++;
                                            }
                                            else
                                            {
                                                if (comparer.Compare(existingOhlc.lastSwap, new HexDataSet { BlockNumber = decoded.Log.BlockNumber, TransactionIndex = decoded.Log.TransactionIndex, LogIndex = decoded.Log.LogIndex }) > 0)
                                                {
                                                    existingOhlc.Open = price;
                                                    existingOhlc.Low = Math.Min(existingOhlc.Low, price);
                                                    existingOhlc.High = Math.Max(existingOhlc.High, price);
                                                    existingOhlc.Volume += quantity;
                                                    existingOhlc.lastSwap = new HexDataSet
                                                    {
                                                        BlockNumber = decoded.Log.BlockNumber,
                                                        TransactionIndex = decoded.Log.TransactionIndex,
                                                        LogIndex = decoded.Log.LogIndex
                                                    };
                                                    if (tdt.ohlc.Count > 1)
                                                    {
                                                        Ohlc currentOhlc = tdt.ohlc[tdt.ohlc.Count - 1];
                                                        Ohlc lastOhlc = tdt.ohlc[tdt.ohlc.Count - 2];
                                                        tdt.ohlc.Last().percentage = (currentOhlc.Close - lastOhlc.Close) / lastOhlc.Close * 100;
                                                    }
                                                    existingOhlc.txCounter++;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            await session.StoreAsync(tdt);
                            await session.SaveChangesAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        [Event("Swap")]
        public class SwapEventDTO : IEventDTO
        {
            [Parameter("address", "sender", 1, true)]
            public virtual string Sender { get; set; }
            [Parameter("uint256", "amount0In", 2, false)]
            public virtual BigInteger Amount0In { get; set; }
            [Parameter("uint256", "amount1In", 3, false)]
            public virtual BigInteger Amount1In { get; set; }
            [Parameter("uint256", "amount0Out", 4, false)]
            public virtual BigInteger Amount0Out { get; set; }
            [Parameter("uint256", "amount1Out", 5, false)]
            public virtual BigInteger Amount1Out { get; set; }
            [Parameter("address", "to", 6, true)]
            public virtual string To { get; set; }
        }
        public class FilterLogBlockNumberTransactionIndexLogIndexComparer : IComparer<HexDataSet>
        {
            public int Compare(HexDataSet x, HexDataSet y)
            {
                if (x.BlockNumber.Value != y.BlockNumber.Value)
                    return x.BlockNumber.Value.CompareTo(y.BlockNumber.Value);
                if (x.TransactionIndex.Value != y.TransactionIndex.Value)
                    return x.TransactionIndex.Value.CompareTo(y.TransactionIndex.Value);
                return x.LogIndex.Value.CompareTo(y.LogIndex.Value);
            }
        }
    }
}