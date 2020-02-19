using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json.Linq;
using RestSharp;
using WebSocket4Net;
using TradeServer.ScriptingDriver.ScriptApi.Enums;
using TradeServer.ScriptingDriver.ScriptApi.Interfaces;

namespace Haasonline.Public.ExchangeDriver.Bittrex
{
    public class ExchangeDriver : IScriptApi
    {
        private string _publicKey;
        private string _privateKey;
        private string _extra;

        private WebSocket _ws;
        private AutoResetEvent _wsOpened;
        private Dictionary<int, AutoResetEvent> _wsCallbacks = new Dictionary<int, AutoResetEvent>();
        private Dictionary<int, JToken> _wsReplies = new Dictionary<int, JToken>();
        private List<string> _level1Subscriptions = new List<string>();
        private List<string> _level2Subscriptions = new List<string>();
        private bool _authenticated = false;
        private int _i = 0;
        private List<Market> _markets;
        private ApexUserInfo _userInfo;

        private long _lastNonce;
        private readonly string _apiUrl = "https://api.metalx.com";
        private readonly HMACSHA256 _hmac = new HMACSHA256();

        public string PingAddress { get; set; }

        public int PollingSpeed { get; set; }
        public ScriptedExchangeType PlatformType { get; set; }

        public bool HasTickerBatchCalls { get; set; }
        public bool HasOrderbookBatchCalls { get; set; }
        public bool HasLastTradesBatchCalls { get; set; }

        public bool HasPrivateKey { get; set; }
        public bool HasExtraPrivateKey { get; set; }

        public event EventHandler<string> Error;
        public event EventHandler<IScriptTick> PriceUpdate;
        public event EventHandler<IScriptOrderbook> OrderbookUpdate;
        public event EventHandler<IScriptOrderbook> OrderbookCorrection;
        public event EventHandler<IScriptLastTrades> LastTradesUpdate;
        public event EventHandler<Dictionary<string, decimal>> WalletUpdate;
        public event EventHandler<Dictionary<string, decimal>> WalletCorrection;
        public event EventHandler<List<IScriptPosition>> PositionListUpdate;
        public event EventHandler<List<IScriptPosition>> PositionCorrection;
        public event EventHandler<List<IScriptOrder>> OpenOrderListUpdate;
        public event EventHandler<List<IScriptOrder>> OpenOrderCorrection;

        private readonly object _lockObject = new object();

        public ExchangeDriver()
        {
            PingAddress = "http://api.bittrex.com:80";
            PollingSpeed = 1;
            PlatformType = ScriptedExchangeType.Spot;

            HasTickerBatchCalls = false;
            HasOrderbookBatchCalls = false;
            HasLastTradesBatchCalls = false;

            HasPrivateKey = true;
            HasExtraPrivateKey = true;
        }

        public void SetCredentials(string publicKey, string privateKey, string extra)
        {
            _publicKey = publicKey;
            _privateKey = privateKey;
            _extra = extra;
            _hmac.Key = Encoding.UTF8.GetBytes(privateKey);
        }

        public void Connect()
        {
            
        }

        public void Disconnect()
        {
            Console.Out.WriteLine("Disconnect");
        }

        #region Public API
        public List<IScriptMarket> GetMarkets()
        {
            List<IScriptMarket> markets = null;

            try
            {
                JArray instruments = (JArray)Call("GetInstruments", new ApexGetInstruments(1));

                markets = instruments.Select(c => new Market(c as JObject))
                    .ToList<IScriptMarket>();
                _markets = instruments.Select(c => new Market(c as JObject))
                    .ToList();
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return markets;
        }

        public List<IScriptMarket> GetMarginMarkets()
        {
            return null;
        }

        public IScriptTick GetTicker(IScriptMarket market)
        {
            IScriptTick ticker = null;

            try
            {
                Market instrument = _markets.Find(c => c.PrimaryCurrency == market.PrimaryCurrency && c.SecondaryCurrency == market.SecondaryCurrency);
                JObject response = (JObject)Call("GetLevel1", new ApexL1SnapshotRequest(1, instrument.InstrumentId));

                ticker = new Ticker(response, market.PrimaryCurrency, market.SecondaryCurrency);

                if (_level2Subscriptions.Contains(market.PrimaryCurrency + market.SecondaryCurrency) == false)
                {
                    Call("SubscribeLevel1", new ApexSubscribeLevel1(1, instrument.InstrumentId));
                    _level1Subscriptions.Add(market.PrimaryCurrency + market.SecondaryCurrency);
                }
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return ticker;
        }

        public List<IScriptTick> GetAllTickers()
        {
            return null;
        }

        public IScriptOrderbook GetOrderbook(IScriptMarket market)
        {
            IScriptOrderbook orderbook = null;

            try
            {
                Market instrument = _markets.Find(c => c.PrimaryCurrency == market.PrimaryCurrency && c.SecondaryCurrency == market.SecondaryCurrency);
                JArray response = (JArray)Call("GetL2Snapshot", new ApexL2SnapshotRequest(1, instrument.InstrumentId, 50));

                if (response != null)
                    orderbook = new Orderbook(response);

                if (_level2Subscriptions.Contains(market.PrimaryCurrency + market.SecondaryCurrency) == false)
                {
                    Call("SubscribeLevel2", new ApexSubscribeLevel2(1, market.PrimaryCurrency + market.SecondaryCurrency, 100));
                    _level2Subscriptions.Add(market.PrimaryCurrency + market.SecondaryCurrency);
                }
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return orderbook;
        }

        public List<IScriptOrderbook> GetAllOrderbooks()
        {
            return null;
        }

        public IScriptLastTrades GetLastTrades(IScriptMarket market)
        {
            LastTradesContainer trades = null;

            try
            {
                var response = Query(false, "/v1/trade-history?", new Dictionary<string, string>()
                    {
                        {"symbol",market.PrimaryCurrency.ToUpper() + market.SecondaryCurrency.ToUpper()},
                        {"count","100"}
                    });

                if (response != null)
                {
                    trades = new LastTradesContainer();
                    trades.Market = market;
                    trades.Trades = response.Value<JArray>("result")
                        .Select(c => Trade.ParsePublicTrade(market, c as JObject))
                        .Cast<IScriptOrder>()
                        .OrderByDescending(c => c.Timestamp)
                        .ToList();
                }
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return trades;
        }

        public List<IScriptLastTrades> GetAllLastTrades()
        {
            Console.Out.WriteLine("GetAllLastTrades");
            return null;
        }
        #endregion

        #region Private API
        public Dictionary<string, decimal> GetWallet()
        {
            Dictionary<string, decimal> wallet = null;

            try
            {
                if(_authenticated != true)
                {
                    Authenticate();
                }

                JArray response = (JArray)Call("GetAccountPositions", new ApexGetAccountPositions(1, _userInfo.AccountId));

                if (response != null)
                    wallet = response.Where(c => Convert.ToDecimal(c.Value<string>("Amount"), CultureInfo.InvariantCulture) > 0.0M)
                        .ToDictionary(c => c.Value<string>("ProductSymbol"), c => Convert.ToDecimal(c.Value<string>("Amount"), CultureInfo.InvariantCulture) - Convert.ToDecimal(c.Value<string>("Hold"), CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return wallet;
        }

        public IScriptMarginWallet GetMarginWallet()
        {
            return null;
        }

        public List<IScriptOrder> GetOpenOrders()
        {
            List<IScriptOrder> orders = null;

            try
            {
                if (_authenticated != true)
                {
                    Authenticate();
                }

                if (_markets == null)
                {
                    GetMarkets();
                }

                JArray response = (JArray)Call("GetOpenOrders", new ApexGetOpenOrders(1, _userInfo.AccountId));

                if (response != null)
                {
                    orders = response.Select(item => Order.ParseOpenOrder(item as JObject, _markets))
                    .Cast<IScriptOrder>()
                    .ToList();
                }
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return orders;
        }

        public List<IScriptPosition> GetPositions()
        {
            return null;
        }

        public List<IScriptOrder> GetTradeHistory()
        {
            List<IScriptOrder> trades = null;

            try
            {
                var parameters = new Dictionary<string, string>() {
                    { "count", "1000" }
                };

                var response = Query(true, "/v1/account/order-history?", parameters);

                if (response != null && response.Value<bool>("success"))
                {
                    trades = response.Value<JArray>("result")
                        .Select(item => Trade.ParsePrivateTrades(item as JObject))
                        .Cast<IScriptOrder>()
                        .ToList();
                }
            }
            catch (Exception e)
            {
                OnError(e.Message);
            }

            return trades;
        }

        public string PlaceOrder(IScriptMarket market, ScriptedOrderType direction, decimal price, decimal amount, bool isMarketOrder, string template = "", bool hiddenOrder = false)
        {
            var result = "";

            Console.Out.WriteLine("PlaceOrder1");

            try
            {
                Market instrument = _markets.Find(c => c.PrimaryCurrency == market.PrimaryCurrency && c.SecondaryCurrency == market.SecondaryCurrency);
                int side = (direction == ScriptedOrderType.Buy) ? 0 : 1;
                int type = (isMarketOrder) ? ApexOrderType.Market : ApexOrderType.Limit;

                JObject response = (JObject)Call("SendOrder", new ApexSendOrder(instrument.InstrumentId, 1, _userInfo.AccountId, side, amount.ToString(), type, price.ToString()));

                result = response.Value<string>("OrderId");
            } catch (Exception e)
            {
                OnError(e.Message);
            }

            return result;
        }
        public string PlaceOrder(IScriptMarket market, ScriptedLeverageOrderType direction, decimal price, decimal amount, decimal leverage, bool isMarketOrder, string template = "", bool isHiddenOrder = false)
        {
            Console.Out.WriteLine("PlaceOrder2");

            return null;
        }

        public bool CancelOrder(IScriptMarket market, string orderId, bool isBuyOrder)
        {
            var result = false;

            try
            {
                JObject response = (JObject)Call("CancelOrder", new ApexCancelOrder(1, _userInfo.AccountId, int.Parse(orderId)));

                result = response.Value<bool>("result");
            } catch (Exception e)
            {
                OnError(e.Message);
            }

            return result;
        }

        public ScriptedOrderStatus GetOrderStatus(string orderId, IScriptMarket scriptMarket, decimal price, decimal amount, bool isBuyOrder)
        {
            var status = ScriptedOrderStatus.Unkown;

            Console.Out.WriteLine("GetOrderStatus");

            return status;
        }
        public IScriptOrder GetOrderDetails(string orderId, IScriptMarket market, decimal price, decimal amount, bool isBuyOrder)
        {
            // This might be called even when the order is in the open orders.
            // Make sure that the order is not open.

            Order order = null;

            Console.Out.WriteLine("GetOrderDetails");

            return order;
        }
        #endregion

        #region Helpers
        public decimal GetContractValue(IScriptMarket pair, decimal price)
        {
            return 1;
        }

        public decimal GetMaxPositionAmount(IScriptMarket pair, decimal tickClose, Dictionary<string, decimal> wallet, decimal leverage, ScriptedLeverageSide scriptedLeverageSide)
        {
            return 1;
        }


        private void GetOrderDetailsFromTrades(IScriptMarket market, string orderId, decimal amount, Order order, List<IScriptOrder> trades)
        {
            order.OrderId = orderId;
            order.Market = market;

            if (order.Status == ScriptedOrderStatus.Unkown)
                order.Status = trades.Sum(c => c.AmountFilled) >= amount
                    ? ScriptedOrderStatus.Completed
                    : ScriptedOrderStatus.Cancelled;

            order.Price = GetAveragePrice(trades.ToList());
            order.Amount = amount;
            order.AmountFilled = trades.Sum(c => c.AmountFilled);
            order.AmountFilled = Math.Min(order.Amount, order.AmountFilled);

            order.FeeCost = trades.Sum(c => c.FeeCost);

            if (!trades.Any())
                return;

            order.Timestamp = trades.First().Timestamp;
            order.FeeCurrency = trades[0].FeeCurrency;
            order.IsBuyOrder = trades[0].IsBuyOrder;
        }

        private decimal GetAveragePrice(List<IScriptOrder> trades)
        {
            var totalVolume = trades.Sum(c => c.AmountFilled * c.Price);
            if (totalVolume == 0 || trades.Sum(c => c.AmountFilled) == 0M)
                return 0M;

            return totalVolume / trades.Sum(c => c.AmountFilled);
        }
        #endregion

        #region Events
        private void OnError(string exMessage)
        {
            if (Error != null)
                Error(this, exMessage);
        }

        private void OnPriceUpdate(IScriptTick e)
        {
            if (PriceUpdate != null)
                PriceUpdate(this, e);
        }
        private void OnOrderbookUpdate(IScriptOrderbook e)
        {
            if (OrderbookUpdate != null)
                OrderbookUpdate(this, e);
        }
        private void OnOrderbookCorrection(IScriptOrderbook e)
        {
            if (OrderbookCorrection != null)
                OrderbookCorrection(this, e);
        }
        private void OnLastTradesUpdate(IScriptLastTrades e)
        {
            if (LastTradesUpdate != null)
                LastTradesUpdate(this, e);
        }

        private void OnWalletUpdate(Dictionary<string, decimal> e)
        {
            if (WalletUpdate != null)
                WalletUpdate(this, e);
        }
        private void OnWalletCorrection(Dictionary<string, decimal> e)
        {
            if (WalletCorrection != null)
                WalletCorrection(this, e);
        }

        private void OnOpenOrderListUpdate(List<IScriptOrder> e)
        {
            if (OpenOrderListUpdate != null)
                OpenOrderListUpdate(this, e);
        }
        private void OnOpenOrderCorrection(List<IScriptOrder> e)
        {
            if (OpenOrderCorrection != null)
                OpenOrderCorrection(this, e);
        }

        private void OnPositionListUpdate(List<IScriptPosition> e)
        {
            if (PositionListUpdate != null)
                PositionListUpdate(this, e);
        }
        private void OnPositionCorrection(List<IScriptPosition> e)
        {
            if (PositionCorrection != null)
                PositionCorrection(this, e);
        }
        #endregion

        #region Reset API Functions
        private void SocketConnected(object sender, EventArgs e)
        {            
            _wsOpened.Set();
        }

        private void SocketDisconnected(object sender, EventArgs e)
        {
            _i = 0;
            _level1Subscriptions = new List<string>();
            _level2Subscriptions = new List<string>();
        }

        private void HandleMessage(object sender, MessageReceivedEventArgs e)
        {
            ApexResponse response = JToken.Parse(e.Message).ToObject<ApexResponse>();

            if (response.m == 1)
            {
                if (_wsCallbacks.ContainsKey(response.i) == true)
                {
                    AutoResetEvent callback = null;

                    if(_wsCallbacks.TryGetValue(response.i, out callback) == true)
                    {
                        _wsReplies.Add(response.i, JToken.Parse(response.o));
                        callback.Set();
                    }
                }
            } else if (response.m == 3)
            {
                if (response.n == "Level1UpdateEvent")
                {
                    Console.Out.WriteLine(e.Message);
                } else if(response.n == "Level2UpdateEvent")
                {
                    OnOrderbookUpdate(new Orderbook(JArray.Parse(response.o)));
                } else
                {
                    Console.Out.WriteLine(e.Message);
                }
            } else if (response.m == 5)
            {
                Console.Out.WriteLine(e.Message);

                AutoResetEvent callback = null;

                if (_wsCallbacks.TryGetValue(response.i, out callback) == true)
                {
                    _wsReplies.Add(response.i, null);
                    callback.Set();
                }
            }
        }

        private JToken Call(string method, Object body)
        {
            if (Monitor.TryEnter(_lockObject, 30000))
                try
                {
                    if(_ws == null)
                    {
                        _ws = new WebSocket("wss://apimetalpayprod.alphapoint.com/WSGateway/");
                        _ws.MessageReceived += HandleMessage;
                        _ws.Opened += SocketConnected;
                        _ws.Closed += SocketDisconnected;
                    }

                    if(_ws.State != WebSocketState.Open && _ws.State != WebSocketState.Connecting)
                    {
                        _wsOpened = new AutoResetEvent(false);
                        _ws.Open();
                        _wsOpened.WaitOne();
                    }

                    ApexRequest request = null;

                    if(body != null)
                    {
                        request = new ApexRequest(0, _i, method, JToken.FromObject(body).ToString(Newtonsoft.Json.Formatting.None));
                    } else
                    {
                        request = new ApexRequest(0, _i, method, null);
                    }

                    JToken jsonRequest = JToken.FromObject(request);
                    AutoResetEvent callback = new AutoResetEvent(false);
                    _wsCallbacks.Add(_i, callback);
                    _i = _i + 2;
                    _ws.Send(jsonRequest.ToString());
                    callback.WaitOne();
                    _wsCallbacks.Remove(request.i);
                    JToken response = null;
                    _wsReplies.TryGetValue(request.i, out response);

                    return response;
                }
                catch (Exception ex)
                {
                    OnError(ex.Message);
                }
                finally
                {
                    Monitor.Exit(_lockObject);
                }

            return null;
        }

        private void Authenticate()
        {
            string userId = _extra;
            string apiKey = _publicKey;
            string nonce = GetNonce().ToString();
            var data = Encoding.UTF8.GetBytes(nonce + userId + apiKey);
            string signature = ByteToString(_hmac.ComputeHash(data)).ToLower();

            JObject response = (JObject)Call("AuthenticateUser", new ApexAuthenticateUser(apiKey, signature, userId, nonce));

            if(response.Value<bool>("Authenticated") == true)
            {
                _authenticated = true;
                _userInfo = new ApexUserInfo(response.Value<JObject>("User"));
                Call("SubscribeAccountEvents", )
            } else
            {
                throw new Exception("Failed to authenticate");
            }
        }

        private JToken Query(bool authenticate, string methodName, Dictionary<string, string> args = null)
        {
            if (args == null)
                args = new Dictionary<string, string>();

            var dataStr = BuildPostData(args);

            if (Monitor.TryEnter(_lockObject, 30000))
                try
                {
                    string url = _apiUrl + methodName + dataStr;

                    RestClient client = new RestClient(url);
                    RestRequest request = new RestRequest();

                    if (authenticate)
                    {
                        string nonce = GetNonce().ToString();
                        string signatureData = nonce;
                        var data = Encoding.UTF8.GetBytes(signatureData);
                        request.AddHeader("x-nonce", nonce);
                        request.AddHeader("x-key", this._publicKey);
                        request.AddHeader("x-user", this._extra);
                        request.AddHeader("x-signature", ByteToString(_hmac.ComputeHash(data)).ToLower());
                    }

                    var response = client.Execute(request).Content;
                    return JToken.Parse(response);
                }
                catch (Exception ex)
                {
                    OnError(ex.Message);
                }
                finally
                {
                    Monitor.Exit(_lockObject);
                }

            return null;
        }

        private static string BuildPostData(Dictionary<string, string> d)
        {
            string s = "";
            for (int i = 0; i < d.Count; i++)
            {
                var item = d.ElementAt(i);
                var key = item.Key;
                var val = item.Value;

                s += key + "=" + HttpUtility.UrlEncode(val);

                if (i != d.Count - 1)
                    s += "&";
            }
            return s;
        }
        private Int64 GetNonce()
        {
            var temp = DateTime.UtcNow.Ticks;
            if (temp <= _lastNonce)
                temp = _lastNonce + 1;
            _lastNonce = temp;
            return _lastNonce;
        }

        private static string ByteToString(byte[] buff)
        {
            return buff.Aggregate("", (current, t) => current + t.ToString("X2"));
        }
        public static byte[] StringToByteArray(String hex)
        {
            var numberChars = hex.Length;
            var bytes = new byte[numberChars / 2];
            for (var i = 0; i < numberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
        #endregion
    }

    public class Market : IScriptMarket
    {
        public int InstrumentId { get; set; }
        public string PrimaryCurrency { get; set; }
        public string SecondaryCurrency { get; set; }
        public decimal Fee { get; set; }
        public int PriceDecimals { get; set; }
        public int AmountDecimals { get; set; }
        public decimal MinimumTradeAmount { get; set; }
        public decimal MinimumTradeVolume { get; set; }

        // Not relavent for spot
        public DateTime SettlementDate { get; set; }
        public List<decimal> Leverage { get; set; }
        public string UnderlyingCurrency { get; set; }
        public string ContractName { get; set; }

        public Market(JObject data)
        {
            try
            {
                SettlementDate = DateTime.Now;
                Leverage = new List<decimal>();
                ContractName = "";

                InstrumentId = data.Value<int>("InstrumentId");
                PrimaryCurrency = data.Value<string>("Product1Symbol");
                SecondaryCurrency = data.Value<string>("Product2Symbol");
                UnderlyingCurrency = PrimaryCurrency;
                Fee = 0.25M;
                PriceDecimals = 8;
                MinimumTradeVolume = 0.0005M;

                string minTradeSize = "1";
                MinimumTradeAmount = Decimal.Parse(minTradeSize, NumberStyles.Float, CultureInfo.InvariantCulture);

                AmountDecimals = 1;
                if (minTradeSize.Contains("."))
                    AmountDecimals = minTradeSize.Split('.')[1].Length;
                else
                    AmountDecimals = 0;
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public Market(string primaryCurrency, string secondaryCurrency)
        {
            PrimaryCurrency = primaryCurrency;
            SecondaryCurrency = secondaryCurrency;
        }

        public virtual decimal ParsePrice(decimal price)
        {
            return Math.Round(price, PriceDecimals);
        }

        public virtual decimal ParseAmount(decimal price)
        {
            return Math.Round(price, AmountDecimals);
        }

        public virtual int GetPriceDecimals(decimal price)
        {
            return PriceDecimals;
        }

        public virtual int GetAmountDecimals(decimal price)
        {
            return AmountDecimals;
        }

        public bool IsAmountEnough(decimal price, decimal amount)
        {
            return amount > MinimumTradeAmount && amount * price >= MinimumTradeVolume;
        }
    }

    public class Ticker : IScriptTick
    {
        public IScriptMarket Market { get; set; }

        public decimal Close { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal SellPrice { get; set; }

        public Ticker(JObject o, string primairy = "", string secondairy = "")
        {
            var close = o.Value<string>("SessionClose");
            var buyPrice = o.Value<string>("BestOffer");
            var sellPrice = o.Value<string>("BestBid");

            Market = new Market(primairy, secondairy);

            if (close != null)
                Close = Decimal.Parse(close, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (buyPrice != null)
                BuyPrice = Decimal.Parse(buyPrice, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (sellPrice != null)
                SellPrice = Decimal.Parse(sellPrice, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    public class Orderbook : IScriptOrderbook
    {
        public List<IScriptOrderbookRecord> Asks { get; set; }
        public List<IScriptOrderbookRecord> Bids { get; set; }

        public Orderbook(JArray o)
        {
            Bids = o.Where(c => c.Value<int>(9) == 0)
                .Select(c => OrderInfo.Parse(c as JArray))
                .ToList<IScriptOrderbookRecord>();

            Asks = o.Where(c => c.Value<int>(9) == 1)
                .Select(c => OrderInfo.Parse(c as JArray))
                .ToList<IScriptOrderbookRecord>();
        }
    }

    public class OrderInfo : IScriptOrderbookRecord
    {
        public decimal Price { get; set; }
        public decimal Amount { get; set; }

        public static OrderInfo Parse(JArray o)
        {
            if (o == null)
                return null;

            var r = new OrderInfo()
            {
                Price = Decimal.Parse(o.Value<string>(6), NumberStyles.Float, CultureInfo.InvariantCulture),
                Amount = Decimal.Parse(o.Value<string>(8), NumberStyles.Float, CultureInfo.InvariantCulture)
            };

            return r;
        }
    }

    public class LastTradesContainer : IScriptLastTrades
    {
        public IScriptMarket Market { get; set; }
        public List<IScriptOrder> Trades { get; set; }
    }

    public class Order : IScriptOrder
    {
        public IScriptMarket Market { get; set; }
        public string OrderId { get; set; }
        public string ExecutingId { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal Price { get; set; }
        public decimal Amount { get; set; }
        public decimal AmountFilled { get; set; }
        public decimal FeeCost { get; set; }
        public string FeeCurrency { get; set; }
        public bool IsBuyOrder { get; set; }
        public ScriptedLeverageOrderType Direction { get; set; }
        public ScriptedOrderStatus Status { get; set; }
        public string ExtraInfo1 { get; set; }

        public Order()
        {
            Status = ScriptedOrderStatus.Unkown;
        }

        public static Order ParseOpenOrder(JObject o, List<Market> markets)
        {
            if (o == null)
                return null;

            Market market = markets.Find(c => c.InstrumentId == o.Value<int>("Instrument"));

            var r = new Order()
            {
                Market = market,
                OrderId = o.Value<string>("OrderId"),
                Price = o.Value<decimal>("Price"),
                Amount = o.Value<decimal>("Quantity"),
                AmountFilled = o.Value<decimal>("QuantityExecuted"),

                Status = ScriptedOrderStatus.Executing
            };

            if (o.Value<string>("Side") != null)
                r.IsBuyOrder = o.Value<string>("Side").ToLower() == "buy";


            return r;
        }

        public static Order ParseSingle(JObject o)
        {
            if (o == null)
                return null;

            Order order = ParseOpenOrder(o, null);
            order.IsBuyOrder = o.Value<string>("Type").IndexOf("limit_buy", StringComparison.Ordinal) > 0;

            if (Convert.ToDecimal(o.Value<string>("QuantityRemaining"), CultureInfo.InvariantCulture) == 0.0M)
                order.Status = ScriptedOrderStatus.Completed;

            else if (!o.Value<bool>("IsOpen"))
                order.Status = ScriptedOrderStatus.Cancelled;

            else if (o.Value<bool>("IsOpen"))
                order.Status = ScriptedOrderStatus.Executing;

            if (o.Property("CancelInitiated") != null && o.Value<bool>("CancelInitiated"))
                order.Status = ScriptedOrderStatus.Cancelled;

            return order;
        }
    }

    public class Trade : IScriptOrder
    {
        public IScriptMarket Market { get; set; }
        public string OrderId { get; set; }
        public string ExecutingId { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal Price { get; set; }
        public decimal Amount { get; set; }
        public decimal AmountFilled { get; set; }
        public decimal FeeCost { get; set; }
        public string FeeCurrency { get; set; }
        public bool IsBuyOrder { get; set; }
        public ScriptedLeverageOrderType Direction { get; set; }
        public ScriptedOrderStatus Status { get; set; }

        public static Trade ParsePrivateTrades(JObject o)
        {
            if (o == null)
                return null;

            string[] pair = o.Value<string>("Exchange").Split('-');

            var r = new Trade()
            {
                Market = new Market(pair[1], pair[0]),

                OrderId = o.Value<string>("OrderUuid"),
                Timestamp = DateTime.ParseExact(o.Value<string>("Closed"), "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture),

                Price = Decimal.Parse(o.Value<string>("Price"), NumberStyles.Float, CultureInfo.InvariantCulture),
                Amount = Decimal.Parse(o.Value<string>("Quantity"), NumberStyles.Float, CultureInfo.InvariantCulture),
                AmountFilled = Decimal.Parse(o.Value<string>("Quantity"), NumberStyles.Float, CultureInfo.InvariantCulture) - Decimal.Parse(o.Value<string>("QuantityRemaining"), NumberStyles.Float, CultureInfo.InvariantCulture),

                FeeCurrency = pair[0],
                FeeCost = Decimal.Parse(o.Value<string>("Commission"), NumberStyles.Float, CultureInfo.InvariantCulture),

                IsBuyOrder = o.Value<string>("OrderType").ToLower().IndexOf("buy", StringComparison.Ordinal) > -1,
            };

            // Bittrex send total pays price. Small correction to be made here.
            r.Price = Math.Round(r.Price / r.Amount, 8);

            return r;
        }

        public static Trade ParsePublicTrade(IScriptMarket market, JObject o)
        {
            if (o == null)
                return null;

            var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var r = new Trade()
            {
                Market = market,
                Timestamp = time.AddMilliseconds(o.Value<double>("time")),
                Price = Decimal.Parse(o.Value<string>("price"), NumberStyles.Float, CultureInfo.InvariantCulture),
                Amount = Decimal.Parse(o.Value<string>("quantity"), NumberStyles.Float, CultureInfo.InvariantCulture),
                IsBuyOrder = o.Value<string>("side").ToLower() == "buy"
            };

            return r;
        }
    }

    public class ApexRequest
    {
        public int m { get; set; }
        public int i { get; set; }
        public string n { get; set; }
        public string o { get; set; }

        public ApexRequest(int m, int i, string n, string o)
        {
            this.m = m;
            this.i = i;
            this.n = n;
            this.o = o;
        }
    }

    public class ApexResponse
    {
        public int m { get; set; }
        public int i { get; set; }
        public string n { get; set; }
        public string o { get; set; }

        public ApexResponse(int m, int i, string n, string o)
        {
            this.m = m;
            this.i = i;
            this.n = n;
            this.o = o;
        }
    }

    public class ApexL1SnapshotRequest
    {
        public int OMSId { get; set; }
        public int InstrumentId { get; set; }

        public ApexL1SnapshotRequest(int OMSId, int InstrumentId)
        {
            this.OMSId = OMSId;
            this.InstrumentId = InstrumentId;
        }
    }

    public class ApexL2SnapshotRequest
    {
        public int OMSId { get; set; }
        public int InstrumentId { get; set; }
        public int Depth { get; set; }

        public ApexL2SnapshotRequest(int OMSId, int InstrumentId, int Depth)
        {
            this.OMSId = OMSId;
            this.InstrumentId = InstrumentId;
            this.Depth = Depth;
        }
    }

    public class ApexGetInstruments
    {
        public int OMSId { get; set; }

        public ApexGetInstruments(int OMSId)
        {
            this.OMSId = OMSId;
        }
    }

    public class ApexSubscribeLevel1
    {
        public int OMSId { get; set; }
        public int InstrumentId { get; set; }

        public ApexSubscribeLevel1(int OMSId, int InstrumentId)
        {
            this.OMSId = OMSId;
            this.InstrumentId = InstrumentId;
        }
    }

    public class ApexSubscribeLevel2
    {
        public int OMSId { get; set; }
        public string Symbol { get; set; }
        public int Depth { get; set; }

        public ApexSubscribeLevel2(int OMSId, string Symbol, int Depth)
        {
            this.OMSId = OMSId;
            this.Symbol = Symbol;
            this.Depth = Depth;
        }
    }

    public class ApexAuthenticateUser
    {
        public string APIKey { get; set; }
        public string Signature { get; set; }
        public string UserId { get; set; }
        public string Nonce { get; set; }

        public ApexAuthenticateUser(string APIKey, string Signature, string UserId, string Nonce)
        {
            this.APIKey = APIKey;
            this.Signature = Signature;
            this.UserId = UserId;
            this.Nonce = Nonce;
        }
    }

    public class ApexGetAccountPositions
    {
        public int OMSId { get; set; }
        public int accountId { get; set; }

        public ApexGetAccountPositions(int OMSId, int accountId)
        {
            this.OMSId = OMSId;
            this.accountId = accountId;
        }
    }

    public class ApexGetOpenOrders
    {
        public int OMSId { get; set; }
        public int AccountId { get; set; }

        public ApexGetOpenOrders(int OMSId, int AccountId)
        {
            this.OMSId = OMSId;
            this.AccountId = AccountId;
        }
    }

    public class ApexCancelOrder
    {
        public int OMSId { get; set; }
        public int AccountId { get; set; }
        public int OrderId { get; set; }

        public ApexCancelOrder(int OMSId, int AccountId, int OrderId)
        {
            this.OMSId = OMSId;
            this.AccountId = AccountId;
            this.OrderId = OrderId;
        }
    }

    public class ApexOrderType
    {
        public static int Market = 1;
        public static int Limit = 2;
    }

    public class ApexSendOrder
    {
        public int InstrumentId { get; set; }
        public int OMSId { get; set; }
        public int AccountId { get; set; }
        public int TimeInForce { get; set; }
        public int ClientOrderId { get; set; }
        public int OrderIdOCO { get; set; }
        public int TimeInOrder { get; set; }
        public bool UseDisplayQuantity { get; set; }
        public int Side { get; set; }
        public string Quantity { get; set; }
        public int OrderType { get; set; }
        public int PegPriceType { get; set; }
        public string LimitPrice { get; set; }

        public ApexSendOrder(int InstrumentId, int OMSId, int AccountId, int Side, string Quantity, int OrderType, string LimitPrice)
        {
            this.InstrumentId = InstrumentId;
            this.OMSId = OMSId;
            this.AccountId = AccountId;
            this.TimeInForce = 1;
            this.ClientOrderId = 0;
            this.OrderIdOCO = 0;
            this.TimeInOrder = 0;
            this.UseDisplayQuantity = false;
            this.Side = Side;
            this.Quantity = Quantity;
            this.OrderType = OrderType;
            this.PegPriceType = 3;
            this.LimitPrice = LimitPrice;
        }
    }

    public class ApexSubscribeAccountEvents
    {
        public int OMSId { get; set; }
        public int AccountId { get; set; }

        public ApexSubscribeAccountEvents(int OMSId, int AccountId)
        {
            this.OMSId = OMSId;
            this.AccountId = AccountId;
        }
    }

    public class ApexInstrument
    {
        public int InstrumentId { get; set; }
        public string Symbol { get; set; }
    }

    public class ApexUserInfo
    {
        public int AccountId { get; set; }

        public ApexUserInfo(JObject o)
        {
            this.AccountId = o.Value<int>("AccountId");
        }
    }
}