﻿namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Common;
	using Ecng.Collections;

	using StockSharp.Messages;
	using StockSharp.Logging;

	/// <summary>
	/// Transactional messages ordering adapter.
	/// </summary>
	public class TransactionOrderingMessageAdapter : MessageAdapterWrapper
	{
		private class SubscriptionInfo
		{
			public SubscriptionInfo(OrderStatusMessage original)
			{
				Original = original ?? throw new System.ArgumentNullException(nameof(original));
			}

			public SyncObject Sync { get; } = new SyncObject();

			public OrderStatusMessage Original { get; }
			public Dictionary<long, Tuple<ExecutionMessage, List<ExecutionMessage>>> Transactions { get; } = new Dictionary<long, Tuple<ExecutionMessage, List<ExecutionMessage>>>();
		}

		private readonly SynchronizedDictionary<long, SubscriptionInfo> _transactionLogSubscriptions = new SynchronizedDictionary<long, SubscriptionInfo>();
		private readonly SynchronizedSet<long> _orderStatusIds = new SynchronizedSet<long>();
		
		private readonly SynchronizedDictionary<long, long> _orders = new SynchronizedDictionary<long, long>();
		private readonly SynchronizedDictionary<long, SecurityId> _secIds = new SynchronizedDictionary<long, SecurityId>();

		private readonly SynchronizedPairSet<long, long> _orderIds = new SynchronizedPairSet<long, long>();
		private readonly SynchronizedPairSet<string, long> _orderStringIds = new SynchronizedPairSet<string, long>(StringComparer.InvariantCultureIgnoreCase);
		
		private readonly SyncObject _nonAssociatedLock = new SyncObject();
		private readonly Dictionary<long, List<ExecutionMessage>> _nonAssociatedOrderIds = new Dictionary<long, List<ExecutionMessage>>();
		private readonly Dictionary<string, List<ExecutionMessage>> _nonAssociatedStringOrderIds = new Dictionary<string, List<ExecutionMessage>>();

		/// <summary>
		/// Initializes a new instance of the <see cref="TransactionOrderingMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Inner message adapter.</param>
		public TransactionOrderingMessageAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
		}

		private void Reset()
		{
			_transactionLogSubscriptions.Clear();
			_orderStatusIds.Clear();
			
			_orders.Clear();
			_secIds.Clear();

			_orderIds.Clear();
			_orderStringIds.Clear();

			lock (_nonAssociatedLock)
			{
				_nonAssociatedOrderIds.Clear();
				_nonAssociatedStringOrderIds.Clear();
			}
		}

		/// <inheritdoc />
		public override bool SendInMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Reset:
				{
					Reset();
					break;
				}
				case MessageTypes.OrderRegister:
				{
					var regMsg = (OrderRegisterMessage)message;
					_secIds.TryAdd(regMsg.TransactionId, regMsg.SecurityId);
					break;
				}
				case MessageTypes.OrderReplace:
				{
					var replaceMsg = (OrderReplaceMessage)message;

					if (_secIds.TryGetValue(replaceMsg.OriginalTransactionId, out var secId))
						_secIds.TryAdd(replaceMsg.TransactionId, secId);

					break;
				}
				case MessageTypes.OrderPairReplace:
				{
					var replaceMsg = (OrderPairReplaceMessage)message;

					if (_secIds.TryGetValue(replaceMsg.Message1.OriginalTransactionId, out var secId))
						_secIds.TryAdd(replaceMsg.Message1.TransactionId, secId);

					if (_secIds.TryGetValue(replaceMsg.Message2.OriginalTransactionId, out secId))
						_secIds.TryAdd(replaceMsg.Message2.TransactionId, secId);

					break;
				}
				case MessageTypes.OrderStatus:
				{
					var statusMsg = (OrderStatusMessage)message;

					if (statusMsg.IsSubscribe)
					{
						if (IsSupportTransactionLog)
							_transactionLogSubscriptions.Add(statusMsg.TransactionId, new SubscriptionInfo(statusMsg.TypedClone()));
						else
							_orderStatusIds.Add(statusMsg.TransactionId);
					}

					break;
				}
			}

			return base.SendInMessage(message);
		}

		/// <inheritdoc />
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			var processSuspended = false;

			switch (message.Type)
			{
				case MessageTypes.SubscriptionResponse:
				{
					var responseMsg = (SubscriptionResponseMessage)message;

					if (responseMsg.Error != null)
					{
						_transactionLogSubscriptions.Remove(responseMsg.OriginalTransactionId);
					}

					break;
				}
				case MessageTypes.SubscriptionFinished:
				case MessageTypes.SubscriptionOnline:
				{
					var originMsg = (IOriginalTransactionIdMessage)message;

					if (!_transactionLogSubscriptions.TryGetAndRemove(originMsg.OriginalTransactionId, out var subscription))
						break;

					Tuple<ExecutionMessage, List<ExecutionMessage>>[] tuples;
					
					lock (subscription.Sync)
						tuples = subscription.Transactions.Values.ToArray();

					foreach (var tuple in tuples)
					{
						var order = tuple.Item1;

						base.OnInnerAdapterNewOutMessage(order);

						ProcessSuspended(order);

						foreach (var trade in tuple.Item2)
							base.OnInnerAdapterNewOutMessage(trade);
					}

					break;
				}

				case MessageTypes.Execution:
				{
					var execMsg = (ExecutionMessage)message;

					if (execMsg.IsMarketData())
						break;

					// skip cancellation cause they are reply on action and no have transaction state
					if (execMsg.IsCancellation)
						break;

					var transId = execMsg.TransactionId;

					if (transId != 0)
						_secIds.TryAdd(transId, execMsg.SecurityId);
					else
					{
						if (execMsg.SecurityId == default && _secIds.TryGetValue(execMsg.OriginalTransactionId, out var secId))
							execMsg.SecurityId = secId;
					}

					if (transId != 0 || execMsg.OriginalTransactionId != 0)
					{
						if (transId == 0)
							transId = execMsg.OriginalTransactionId;

						if (execMsg.OrderId != null)
						{
							_orderIds.TryAdd(execMsg.OrderId.Value, transId);
						}
						else if (!execMsg.OrderStringId.IsEmpty())
						{
							_orderStringIds.TryAdd(execMsg.OrderStringId, transId);
						}
					}

					if (execMsg.TransactionId == 0 && execMsg.HasTradeInfo && _orderStatusIds.Contains(execMsg.OriginalTransactionId))
					{
						// below the code will try find order's transaction
						execMsg.OriginalTransactionId = 0;
					}

					if (/*execMsg.TransactionId == 0 && */execMsg.OriginalTransactionId == 0)
					{
						if (!execMsg.HasTradeInfo)
						{
							this.AddWarningLog("Order doesn't have origin trans id: {0}", execMsg);
							break;
						}

						if (execMsg.OrderId != null)
						{
							if (_orderIds.TryGetValue(execMsg.OrderId.Value, out var originId))
								execMsg.OriginalTransactionId = originId;
							else
							{
								this.AddWarningLog("Trade doesn't have origin trans id: {0}", execMsg);
								break;
							}
						}
						else if (!execMsg.OrderStringId.IsEmpty())
						{
							if (_orderStringIds.TryGetValue(execMsg.OrderStringId, out var originId))
								execMsg.OriginalTransactionId = originId;
							else
							{
								this.AddWarningLog("Trade doesn't have origin trans id: {0}", execMsg);
								break;
							}
						}
					}

					if (execMsg.HasTradeInfo && !execMsg.HasOrderInfo)
					{
						if (execMsg.OrderId != null && !_orderIds.ContainsKey(execMsg.OrderId.Value) && (execMsg.OriginalTransactionId == 0 || !_secIds.ContainsKey(execMsg.OriginalTransactionId)))
						{
							this.AddInfoLog("{0} suspended.", execMsg);

							lock (_nonAssociatedLock)
								_nonAssociatedOrderIds.SafeAdd(execMsg.OrderId.Value).Add(execMsg.TypedClone());
							
							return;
						}
						else if (!execMsg.OrderStringId.IsEmpty() && !_orderStringIds.ContainsKey(execMsg.OrderStringId) && (execMsg.OriginalTransactionId == 0 || !_secIds.ContainsKey(execMsg.OriginalTransactionId)))
						{
							this.AddInfoLog("{0} suspended.", execMsg);

							lock (_nonAssociatedLock)
								_nonAssociatedStringOrderIds.SafeAdd(execMsg.OrderStringId).Add(execMsg.TypedClone());

							return;
						}
					}

					if (_transactionLogSubscriptions.Count == 0)
					{
						processSuspended = true;
						break;
					}

					if (!_transactionLogSubscriptions.TryGetValue(execMsg.OriginalTransactionId, out var subscription))
					{
						if (!_orders.TryGetValue(execMsg.OriginalTransactionId, out var orderTransId))
							break;

						if (!_transactionLogSubscriptions.TryGetValue(orderTransId, out subscription))
							break;
					}

					if (transId == 0)
					{
						if (execMsg.HasTradeInfo)
							transId = execMsg.OriginalTransactionId;

						if (transId == 0)
						{
							this.AddWarningLog("Message {0} do not contains transaction id.", execMsg);
							break;
						}
					}

					lock (subscription.Sync)
					{
						if (subscription.Transactions.TryGetValue(transId, out var tuple))
						{
							var snapshot = tuple.Item1;

							if (execMsg.HasOrderInfo)
							{
								if (execMsg.Balance != null)
									snapshot.Balance = snapshot.Balance.ApplyNewBalance(execMsg.Balance.Value, transId, this);

								if (execMsg.OrderState != null)
									snapshot.OrderState = snapshot.OrderState.ApplyNewState(execMsg.OrderState.Value, transId, this);

								if (execMsg.OrderStatus != null)
									snapshot.OrderStatus = execMsg.OrderStatus;

								if (execMsg.OrderId != null)
									snapshot.OrderId = execMsg.OrderId;

								if (!execMsg.OrderStringId.IsEmpty())
									snapshot.OrderStringId = execMsg.OrderStringId;

								if (execMsg.OrderBoardId != null)
									snapshot.OrderBoardId = execMsg.OrderBoardId;

								if (execMsg.PnL != null)
									snapshot.PnL = execMsg.PnL;

								if (execMsg.Position != null)
									snapshot.Position = execMsg.Position;

								if (execMsg.Commission != null)
									snapshot.Commission = execMsg.Commission;

								if (execMsg.CommissionCurrency != null)
									snapshot.CommissionCurrency = execMsg.CommissionCurrency;

								if (execMsg.AveragePrice != null)
									snapshot.AveragePrice = execMsg.AveragePrice;

								if (execMsg.Latency != null)
									snapshot.Latency = execMsg.Latency;
							}
						
							if (execMsg.HasTradeInfo)
							{
								var clone = execMsg.TypedClone();

								// all order's info in snapshot
								execMsg.HasTradeInfo = false;
								clone.HasOrderInfo = false;

								tuple.Item2.Add(clone);
							}
						}
						else
						{
							_orders.Add(transId, execMsg.OriginalTransactionId);
							subscription.Transactions.Add(transId, Tuple.Create(execMsg.TypedClone(), new List<ExecutionMessage>()));
						}
					}

					return;
				}
			}

			base.OnInnerAdapterNewOutMessage(message);

			if (processSuspended)
				ProcessSuspended((ExecutionMessage)message);
		}

		private void ProcessSuspended(ExecutionMessage execMsg)
		{
			if (!execMsg.HasOrderInfo)
				return;

			if (execMsg.OrderId != null)
				ProcessSuspended(_nonAssociatedOrderIds, execMsg.OrderId.Value);

			if (!execMsg.OrderStringId.IsEmpty())
				ProcessSuspended(_nonAssociatedStringOrderIds, execMsg.OrderStringId);
		}

		private void ProcessSuspended<TKey>(Dictionary<TKey, List<ExecutionMessage>> nonAssociated, TKey key)
		{
			List<ExecutionMessage> trades;
			
			lock (_nonAssociatedLock)
			{
				if (nonAssociated.Count > 0)
				{
					if (!nonAssociated.TryGetAndRemove(key, out trades))
						return;
				}
				else
					return;
			}

			this.AddInfoLog("{0} resumed.", key);

			foreach (var trade in trades)
				RaiseNewOutMessage(trade);
		}

		/// <summary>
		/// Create a copy of <see cref="TransactionOrderingMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new TransactionOrderingMessageAdapter(InnerAdapter.TypedClone());
		}
	}
}