﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.OrderBook;
using CryptoExchange.Net.Sockets;

namespace Binance.Net.SymbolOrderBooks
{
    /// <summary>
    /// Implementation for a synchronized order book. After calling Start the order book will sync itself and keep up to date with new data. It will automatically try to reconnect and resync in case of a lost/interrupted connection.
    /// Make sure to check the State property to see if the order book is synced.
    /// </summary>
    public class BinanceSpotSymbolOrderBook : SymbolOrderBook
    {
        private readonly IBinanceClient _restClient;
        private readonly IBinanceSocketClient _socketClient;
        private readonly TimeSpan _initialDataTimeout;
        private readonly bool _restOwner;
        private readonly bool _socketOwner;
        private readonly int? _updateInterval;

        public event Action<DataEvent<IBinanceOrderBook>>? OnOrderBook;
        public event Action<DataEvent<IBinanceEventOrderBook>>? OnOrderBookUpdate;

        /// <summary>
        /// Create a new instance
        /// </summary>
        /// <param name="symbol">The symbol of the order book</param>
        /// <param name="options">The options for the order book</param>
        public BinanceSpotSymbolOrderBook(string symbol, BinanceOrderBookOptions? options = null) : base("Binance", symbol, options ?? new BinanceOrderBookOptions())
        {
            symbol.ValidateBinanceSymbol();
            Levels = options?.Limit;
            _updateInterval = options?.UpdateInterval;
            _initialDataTimeout = options?.InitialDataTimeout ?? TimeSpan.FromSeconds(30);
            _socketClient = options?.SocketClient ?? new BinanceSocketClient();
            _restClient = options?.RestClient ?? new BinanceClient();
            _restOwner = options?.RestClient == null;
            _socketOwner = options?.SocketClient == null;

            sequencesAreConsecutive = options?.Limit == null;
            strictLevels = false;
        }

        /// <inheritdoc />
        protected override async Task<CallResult<UpdateSubscription>> DoStartAsync(CancellationToken ct)
        {
            CallResult<UpdateSubscription> subResult;
            if (Levels == null)
                subResult = await _socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(
                    Symbol, _updateInterval, data =>
                    {
                        HandleUpdate(data);
                        OnOrderBookUpdate?.Invoke(data);
                    }).ConfigureAwait(false);
            else
                subResult = await _socketClient.SpotApi.ExchangeData.SubscribeToPartialOrderBookUpdatesAsync(
                    Symbol, Levels.Value, _updateInterval, data =>
                    {
                        HandleUpdate(data);
                        OnOrderBook?.Invoke(data);
                    }).ConfigureAwait(false);

            if (!subResult)
                return new CallResult<UpdateSubscription>(subResult.Error!);

            if (ct.IsCancellationRequested)
            {
                await subResult.Data.CloseAsync().ConfigureAwait(false);
                return subResult.AsError<UpdateSubscription>(new CancellationRequestedError());
            }

            Status = OrderBookStatus.Syncing;
            if (Levels == null)
            {
                // Small delay to make sure the snapshot is from after our first stream update
                await Task.Delay(200).ConfigureAwait(false);
                var bookResult = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(Symbol, Levels ?? 5000).ConfigureAwait(false);
                if (!bookResult)
                {
                    log.Write(Microsoft.Extensions.Logging.LogLevel.Debug, $"{Id} order book {Symbol} failed to retrieve initial order book");
                    await _socketClient.UnsubscribeAsync(subResult.Data).ConfigureAwait(false);
                    return new CallResult<UpdateSubscription>(bookResult.Error!);
                }

                SetInitialOrderBook(bookResult.Data.LastUpdateId, bookResult.Data.Bids, bookResult.Data.Asks);
                OnOrderBook?.Invoke(new DataEvent<IBinanceOrderBook>(bookResult.Data, DateTime.UtcNow));
            }
            else
            {
                var setResult = await WaitForSetOrderBookAsync(_initialDataTimeout, ct).ConfigureAwait(false);
                return setResult ? subResult : new CallResult<UpdateSubscription>(setResult.Error!);
            }

            return new CallResult<UpdateSubscription>(subResult.Data);
        }

        public void Set(DataEvent<IBinanceOrderBook> data)
        {
            SetInitialOrderBook(data.Data.LastUpdateId, data.Data.Bids, data.Data.Asks);
        }

        public void Update(DataEvent<IBinanceEventOrderBook> data)
        {
            HandleUpdate(data);
        }

        private void HandleUpdate(DataEvent<IBinanceEventOrderBook> data)
        {
            if (data.Data.FirstUpdateId != null)
                UpdateOrderBook(data.Data.FirstUpdateId.Value, data.Data.LastUpdateId, data.Data.Bids, data.Data.Asks);
            else
                UpdateOrderBook(data.Data.LastUpdateId, data.Data.Bids, data.Data.Asks);
        }

        private void HandleUpdate(DataEvent<IBinanceOrderBook> data)
        {
            if (Levels == null)            
                UpdateOrderBook(data.Data.LastUpdateId, data.Data.Bids, data.Data.Asks);            
            else            
                SetInitialOrderBook(data.Data.LastUpdateId, data.Data.Bids, data.Data.Asks);            
        }

        /// <inheritdoc />
        protected override void DoReset()
        {
        }

        /// <inheritdoc />
        protected override async Task<CallResult<bool>> DoResyncAsync(CancellationToken ct)
        {
            if (Levels != null)
                return await WaitForSetOrderBookAsync(_initialDataTimeout, ct).ConfigureAwait(false);

            var bookResult = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(Symbol, Levels ?? 5000).ConfigureAwait(false);
            if (!bookResult)
                return new CallResult<bool>(bookResult.Error!);

            SetInitialOrderBook(bookResult.Data.LastUpdateId, bookResult.Data.Bids, bookResult.Data.Asks);
            OnOrderBook?.Invoke(new DataEvent<IBinanceOrderBook>(bookResult.Data, DateTime.UtcNow));
            return new CallResult<bool>(true);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (_restOwner)
                _restClient?.Dispose();
            if (_socketOwner)
                _socketClient?.Dispose();

            base.Dispose(disposing);
        }
    }
}
