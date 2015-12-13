﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nimbus.Extensions;
using Nimbus.Infrastructure.Dispatching;
using Nimbus.Infrastructure.MessageSendersAndReceivers;

namespace Nimbus.Infrastructure
{
    [DebuggerDisplay("{_receiver}")]
    internal class MessagePump : IMessagePump
    {
        private readonly IClock _clock;
        private readonly IDispatchContextManager _dispatchContextManager;
        private readonly ILogger _logger;
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly INimbusMessageReceiver _receiver;

        private bool _started;
        private readonly SemaphoreSlim _startStopSemaphore = new SemaphoreSlim(1, 1);

        public MessagePump(
            IClock clock,
            IDispatchContextManager dispatchContextManager,
            ILogger logger,
            IMessageDispatcher messageDispatcher,
            INimbusMessageReceiver receiver)
        {
            _clock = clock;
            _dispatchContextManager = dispatchContextManager;
            _logger = logger;
            _messageDispatcher = messageDispatcher;
            _receiver = receiver;
        }

        public async Task Start()
        {
            await _startStopSemaphore.WaitAsync();

            try
            {
                if (_started) return;
                _started = true;

                _logger.Debug("Message pump for {Receiver} starting...", _receiver);
                await _receiver.Start(Dispatch);
                _logger.Debug("Message pump for {Receiver} started", _receiver);
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        public async Task Stop()
        {
            await _startStopSemaphore.WaitAsync();

            try
            {
                if (!_started) return;
                _started = false;

                _logger.Debug("Message pump for {Receiver} stopping...", _receiver);
                await _receiver.Stop();
                _logger.Debug("Message pump for {Receiver} stopped.", _receiver);
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        private async Task Dispatch(NimbusMessage message)
        {
            // Early exit: have we pre-fetched this message and had our lock already expire? If so, just
            // bail - it will already have been picked up by someone else.
            if (message.ExpiresAfter <= _clock.UtcNow)
            {
                _logger.Debug(
                    "Message {MessageId} appears to have already expired so we're not dispatching it. Watch out for clock drift between your service bus server and {MachineName}!",
                    message.MessageId,
                    Environment.MachineName);
                return;
            }

            try
            {
                Exception exception = null;

                try
                {
                    LogInfo("Dispatching", message);
                    using (_dispatchContextManager.StartNewDispatchContext(new SubsequentDispatchContext(message)))
                    {
                        await _messageDispatcher.Dispatch(message);
                    }
                    LogDebug("Dispatched", message);

                    LogDebug("Completing", message);
                    message.Properties[MessagePropertyKeys.DispatchComplete] = true;
                    LogInfo("Completed", message);

                    return;
                }

                catch (Exception exc)
                {
                    _logger.Error(exc,
                                  "Message dispatch failed for {Type} from {QueuePath} [MessageId:{MessageId}, CorrelationId:{CorrelationId}]",
                                  message.SafelyGetBodyTypeNameOrDefault(),
                                  message.ReplyTo,
                                  message.MessageId,
                                  message.CorrelationId);

                    exception = exc;
                }

                try
                {
                    LogDebug("Abandoning", message);
                    await message.AbandonAsync(exception.ExceptionDetailsAsProperties(_clock.UtcNow));
                    LogDebug("Abandoned", message);
                }
                catch (Exception exc)
                {
                    _logger.Error(exc,
                                  "Could not call Abandon() on message {Type} from {QueuePath} [MessageId:{MessageId}, CorrelationId:{CorrelationId}].",
                                  message.SafelyGetBodyTypeNameOrDefault(),
                                  message.MessageId,
                                  message.CorrelationId,
                                  message.ReplyTo);
                }
            }
            catch (Exception exc)
            {
                _logger.Error(exc, "Unhandled exception in message pump");
            }
        }

        private void LogDebug(string activity, NimbusMessage message)
        {
            _logger.Debug("{MessagePumpAction} message {Type} from {QueuePath} [MessageId:{MessageId}, CorrelationId:{CorrelationId}]",
                          activity,
                          message.SafelyGetBodyTypeNameOrDefault(),
                          message.ReplyTo,
                          message.MessageId,
                          message.CorrelationId);
        }

        private void LogInfo(string activity, NimbusMessage message)
        {
            _logger.Info("{MessagePumpAction} message {Type} from {QueuePath} [MessageId:{MessageId}, CorrelationId:{CorrelationId}]",
                         activity,
                         message.SafelyGetBodyTypeNameOrDefault(),
                         message.ReplyTo,
                         message.MessageId,
                         message.CorrelationId);
        }

        public void Dispose()
        {
            // ReSharper disable CSharpWarnings::CS4014
#pragma warning disable 4014
            Stop();
#pragma warning restore 4014
            // ReSharper restore CSharpWarnings::CS4014
        }
    }
}