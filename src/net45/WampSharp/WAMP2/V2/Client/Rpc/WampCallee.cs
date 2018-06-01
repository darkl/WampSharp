using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SystemEx;
using WampSharp.Core.Listener;
using WampSharp.Core.Serialization;
using WampSharp.Core.Utilities;
using WampSharp.V2.Core;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Realm;
using WampSharp.V2.Rpc;

namespace WampSharp.V2.Client
{
    internal class WampCallee<TMessage> : 
        IWampRpcOperationRegistrationProxy, IWampCallee<TMessage>,
        IWampCalleeError<TMessage>
    {
        private readonly IWampServerProxy mProxy;

        private readonly IWampFormatter<TMessage> mFormatter;
        private readonly IWampClientConnectionMonitor mMonitor;

        private readonly WampRequestIdMapper<RegisterRequest> mPendingRegistrations =
            new WampRequestIdMapper<RegisterRequest>();

        private readonly ConcurrentDictionary<long, IWampRpcOperation> mRegistrations =
            new ConcurrentDictionary<long, IWampRpcOperation>();

        private readonly WampRequestIdMapper<UnregisterRequest> mPendingUnregistrations =
            new WampRequestIdMapper<UnregisterRequest>();

        private readonly ConcurrentDictionary<long, InvocationData> mInvocations =
            new ConcurrentDictionary<long, InvocationData>();

        private readonly SwapDictionary<long, SwapCollection<long>> mRegistrationsToInvocations = 
            new SwapDictionary<long, SwapCollection<long>>();

        private readonly object mLock = new object();

        public WampCallee(IWampServerProxy proxy, IWampFormatter<TMessage> formatter, IWampClientConnectionMonitor monitor)
        {
            mProxy = proxy;
            mFormatter = formatter;
            mMonitor = monitor;

            monitor.ConnectionBroken += OnConnectionBroken;
            monitor.ConnectionError += OnConnectionError;
        }

        public Task<IAsyncDisposable> Register(IWampRpcOperation operation, RegisterOptions options)
        {
            if (!IsConnected)
            {
                throw new WampSessionNotEstablishedException();
            }

            RegisterRequest registerRequest =
                new RegisterRequest(operation, mFormatter);

            long id = mPendingRegistrations.Add(registerRequest);

            registerRequest.RequestId = id;

            mProxy.Register(id, options, operation.Procedure);

            return registerRequest.Task;
        }

        public void Registered(long requestId, long registrationId)
        {

            if (mPendingRegistrations.TryRemove(requestId, out RegisterRequest registerRequest))
            {
                mRegistrations[registrationId] = registerRequest.Operation;
                registerRequest.Complete(new UnregisterDisposable(this, registrationId));
            }
        }

        private bool IsConnected => mMonitor.IsConnected;

        private class UnregisterDisposable : IAsyncDisposable
        {
            private readonly WampCallee<TMessage> mCallee;
            private readonly long mRegistrationId;

            public UnregisterDisposable(WampCallee<TMessage> callee, long registrationId)
            {
                mCallee = callee;
                mRegistrationId = registrationId;
            }

            public Task DisposeAsync()
            {
                if (!mCallee.IsConnected)
                {
                    throw new WampSessionNotEstablishedException();
                }

                return mCallee.Unregister(mRegistrationId);
            }
        }

        private Task Unregister(long registrationId)
        {
            UnregisterRequest unregisterRequest =
                new UnregisterRequest(mFormatter);

            long requestId = mPendingUnregistrations.Add(unregisterRequest);

            unregisterRequest.RequestId = requestId;

            mProxy.Unregister(requestId, registrationId);

            return unregisterRequest.Task;
        }

        public void Unregistered(long requestId)
        {

            if (mPendingUnregistrations.TryRemove(requestId, out UnregisterRequest unregisterRequest))
            {
                mRegistrations.TryRemove(requestId, out IWampRpcOperation operation);

                lock (mLock)
                {

                    if (mRegistrationsToInvocations.TryGetValue(requestId, out SwapCollection<long> invocationsToRemove))
                    {
                        mRegistrationsToInvocations.Remove(requestId);

                        foreach (long invocationId in invocationsToRemove)
                        {
                            mInvocations.TryRemove(invocationId, out InvocationData invocation);
                        }
                    }
                }

                unregisterRequest.Complete();
            }
        }

        private IWampRpcOperation TryGetOperation(long registrationId)
        {

            if (mRegistrations.TryGetValue(registrationId, out IWampRpcOperation operation))
            {
                return operation;
            }

            return null;
        }

        private IWampRawRpcOperationRouterCallback GetCallback(long requestId)
        {
            return new ServerProxyCallback(mProxy, requestId);
        }

        public void Invocation(long requestId, long registrationId, InvocationDetails details)
        {
            InvocationPattern(requestId, registrationId, details,
                              (operation, callback, invocationDetails) =>
                                  operation.Invoke(callback,
                                                   mFormatter,
                                                   invocationDetails));
        }

        public void Invocation(long requestId, long registrationId, InvocationDetails details, TMessage[] arguments)
        {
            InvocationPattern(requestId, registrationId, details,
                              (operation, callback, invocationDetails) =>
                                  operation.Invoke(callback,
                                                   mFormatter,
                                                   invocationDetails,
                                                   arguments));
        }

        public void Invocation(long requestId, long registrationId, InvocationDetails details, TMessage[] arguments, IDictionary<string, TMessage> argumentsKeywords)
        {
            InvocationPattern(requestId, registrationId, details,
                              (operation, callback, invocationDetails) =>
                                  operation.Invoke(callback,
                                                   mFormatter,
                                                   invocationDetails,
                                                   arguments,
                                                   argumentsKeywords));
        }

        private void InvocationPattern(long requestId, long registrationId, InvocationDetails details, Func<IWampRpcOperation, IWampRawRpcOperationRouterCallback, InvocationDetails, IWampCancellableInvocation> invocationAction)
        {
            IWampRpcOperation operation = TryGetOperation(registrationId);

            if (operation != null)
            {
                IWampRawRpcOperationRouterCallback callback = GetCallback(requestId);

                InvocationDetails modifiedDetails = new InvocationDetails(details)
                {
                    Procedure = details.Procedure ?? operation.Procedure
                };

                IWampCancellableInvocation invocation = invocationAction(operation, callback, modifiedDetails);

                if (invocation != null)
                {
                    mInvocations[requestId] = new InvocationData(registrationId, invocation);

                    lock (mLock)
                    {
                        mRegistrationsToInvocations.Add(registrationId, requestId);
                    }
                }
            }
        }

        public void RegisterError(long requestId, TMessage details, string error)
        {

            if (mPendingRegistrations.TryRemove(requestId, out RegisterRequest registerRequest))
            {
                registerRequest.Error(details, error);
            }
        }

        public void RegisterError(long requestId, TMessage details, string error, TMessage[] arguments)
        {

            if (mPendingRegistrations.TryRemove(requestId, out RegisterRequest registerRequest))
            {
                registerRequest.Error(details, error, arguments);
            }
        }

        public void RegisterError(long requestId, TMessage details, string error, TMessage[] arguments, TMessage argumentsKeywords)
        {

            if (mPendingRegistrations.TryRemove(requestId, out RegisterRequest registerRequest))
            {
                registerRequest.Error(details, error, arguments, argumentsKeywords);
            }
        }

        public void UnregisterError(long requestId, TMessage details, string error)
        {

            if (mPendingUnregistrations.TryRemove(requestId, out UnregisterRequest unregisterRequest))
            {
                unregisterRequest.Error(details, error);
            }
        }

        public void UnregisterError(long requestId, TMessage details, string error, TMessage[] arguments)
        {

            if (mPendingUnregistrations.TryRemove(requestId, out UnregisterRequest unregisterRequest))
            {
                unregisterRequest.Error(details, error, arguments);
            }
        }

        public void UnregisterError(long requestId, TMessage details, string error, TMessage[] arguments, TMessage argumentsKeywords)
        {

            if (mPendingUnregistrations.TryRemove(requestId, out UnregisterRequest unregisterRequest))
            {
                unregisterRequest.Error(details, error, arguments, argumentsKeywords);
            }
        }

        public void Interrupt(long requestId, InterruptDetails details)
        {

            if (mInvocations.TryRemove(requestId, out InvocationData invocation))
            {
                invocation.Cancellation.Cancel(details);

                lock (mLock)
                {
                    mRegistrationsToInvocations.Remove(invocation.RegistrationId, requestId);
                }
            }
        }

        private class RegisterRequest : WampPendingRequest<TMessage, IAsyncDisposable>
        {
            public RegisterRequest(IWampRpcOperation operation, IWampFormatter<TMessage> formatter) :
                base(formatter)
            {
                Operation = operation;
            }

            public IWampRpcOperation Operation { get; }
        }

        private class UnregisterRequest : WampPendingRequest<TMessage>
        {
            public UnregisterRequest(IWampFormatter<TMessage> formatter) : base(formatter)
            {
            }
        }

        public void OnConnectionError(object sender, WampConnectionErrorEventArgs eventArgs)
        {
            Exception exception = eventArgs.Exception;

            mPendingRegistrations.ConnectionError(exception);
            mPendingUnregistrations.ConnectionError(exception);

            Cleanup();
        }

        public void OnConnectionBroken(object sender, WampSessionCloseEventArgs eventArgs)
        {
            mPendingRegistrations.ConnectionClosed(eventArgs);
            mPendingUnregistrations.ConnectionClosed(eventArgs);
            Cleanup();
        }

        private void Cleanup()
        {
            // TODO: clean up other things?
            mRegistrations.Clear();
            mInvocations.Clear();

            lock (mLock)
            {
                mRegistrationsToInvocations.Clear();
            }
        }

        private class ServerProxyCallback : IWampRawRpcOperationRouterCallback
        {
            private readonly IWampServerProxy mProxy;

            public ServerProxyCallback(IWampServerProxy proxy, long requestId)
            {
                mProxy = proxy;
                RequestId = requestId;
            }

            public long RequestId { get; }

            public void Result<TResult>(IWampFormatter<TResult> formatter, YieldOptions options)
            {
                mProxy.Yield(RequestId, options);
            }

            public void Result<TResult>(IWampFormatter<TResult> formatter, YieldOptions options, TResult[] arguments)
            {
                mProxy.Yield(RequestId, options, arguments.Cast<object>().ToArray());
            }

            public void Result<TResult>(IWampFormatter<TResult> formatter, YieldOptions options, TResult[] arguments, IDictionary<string, TResult> argumentsKeywords)
            {
                mProxy.Yield(RequestId, options, arguments.Cast<object>().ToArray(), argumentsKeywords.ToDictionary(x => x.Key, x => (object)x.Value));
            }

            public void Error<TResult>(IWampFormatter<TResult> formatter, TResult details, string error)
            {
                mProxy.InvocationError(RequestId, details, error);
            }

            public void Error<TResult>(IWampFormatter<TResult> formatter, TResult details, string error, TResult[] arguments)
            {
                mProxy.InvocationError(RequestId, details, error, arguments.Cast<object>().ToArray());
            }

            public void Error<TResult>(IWampFormatter<TResult> formatter, TResult details, string error, TResult[] arguments, TResult argumentsKeywords)
            {
                mProxy.InvocationError(RequestId, details, error, arguments.Cast<object>().ToArray(), argumentsKeywords);
            }
        }

        private class InvocationData
        {
            private readonly long mRegistrationId;

            public InvocationData(long registrationId, IWampCancellableInvocation cancellation)
            {
                Cancellation = cancellation;
                mRegistrationId = registrationId;
            }

            public IWampCancellableInvocation Cancellation { get; }

            public long RegistrationId => mRegistrationId;
        }
    }
}