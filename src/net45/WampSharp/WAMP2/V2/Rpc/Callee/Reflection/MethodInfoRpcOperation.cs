﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WampSharp.Core.Serialization;
using WampSharp.V2.Core.Contracts;

namespace WampSharp.V2.Rpc
{
    public class SyncMethodInfoRpcOperation : SyncLocalRpcOperation
    {
        private readonly object mInstance;
        private readonly MethodInfo mMethod;
        private readonly MethodInfoHelper mHelper;
        private readonly RpcParameter[] mParameters;
        private readonly bool mHasResult;
        private readonly CollectionResultTreatment mCollectionResultTreatment;

        public SyncMethodInfoRpcOperation(object instance, MethodInfo method) :
            base(GetProcedure(method))
        {
            mInstance = instance;
            mMethod = method;

            if (method.ReturnType != typeof (void))
            {
                mHasResult = true;
            }
            else
            {
                mHasResult = false;
            }

            WampResultAttribute wampResultAttribute =
                method.ReturnParameter.GetCustomAttribute<WampResultAttribute>();

            if (wampResultAttribute == null)
            {
                mCollectionResultTreatment = CollectionResultTreatment.Multivalued;
            }
            else
            {
                mCollectionResultTreatment =
                    wampResultAttribute.CollectionResultTreatment;
            }

            mHelper = new MethodInfoHelper(method);

            mParameters =
                method.GetParameters()
                      .Where(x => !x.IsOut)
                      .Select((parameter, index) =>
                              new RpcParameter(parameter.Name,
                                               parameter.ParameterType,
                                               parameter.DefaultValue,
                                               parameter.HasDefaultValue(),
                                               index))
                      .ToArray();
        }

        private static string GetProcedure(MethodInfo method)
        {
            WampProcedureAttribute procedureAttribute =
                method.GetCustomAttribute<WampProcedureAttribute>(true);

            if (procedureAttribute == null)
            {
                // throw 
            }

            return procedureAttribute.Procedure;
        }

        public override RpcParameter[] Parameters
        {
            get { return mParameters; }
        }

        public override bool HasResult
        {
            get { return mHasResult; }
        }

        public override CollectionResultTreatment CollectionResultTreatment
        {
            get { return mCollectionResultTreatment; }
        }

        protected override object InvokeSync<TMessage>
            (IWampRpcOperationCallback caller,
             IWampFormatter<TMessage> formatter,
             TMessage options,
             TMessage[] arguments,
             IDictionary<string, TMessage> argumentsKeywords,
             out IDictionary<string, object> outputs)
        {
            object[] unpacked =
                UnpackParameters(formatter, arguments, argumentsKeywords);

            object[] parameters =
                mHelper.GetArguments(unpacked);

            try
            {
                object result =
                    mMethod.Invoke(mInstance, parameters);

                outputs = mHelper.GetOutOrRefValues(parameters);

                return result;
            }
            catch (TargetInvocationException ex)
            {
                Exception actual = ex.InnerException;

                if (actual is WampException)
                {
                    throw actual;
                }
                else
                {
                    throw ConvertExceptionToRuntimeException(actual);
                }
            }
        }

        private WampRpcRuntimeException ConvertExceptionToRuntimeException(Exception exception)
        {
            // TODO: Maybe try a different implementation.
            return new WampRpcRuntimeException(exception.Message);
        }
    }
}