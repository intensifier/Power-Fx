﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerFx.Core.Binding.BindInfo;
using Microsoft.PowerFx.Core.Entities;
using Microsoft.PowerFx.Core.Functions;
using Microsoft.PowerFx.Core.IR;
using Microsoft.PowerFx.Core.IR.Nodes;
using Microsoft.PowerFx.Core.IR.Symbols;
using Microsoft.PowerFx.Core.Texl;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Functions;
using Microsoft.PowerFx.Interpreter;
using Microsoft.PowerFx.Interpreter.Exceptions;
using Microsoft.PowerFx.Interpreter.Localization;
using Microsoft.PowerFx.Types;
using static Microsoft.PowerFx.Functions.Library;

namespace Microsoft.PowerFx
{
    // This used ValueTask for async, https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/ 
    // Perf comparison of Task vs. ValueTask: https://ladeak.wordpress.com/2019/03/09/valuetask-vs-task 
    // Use Task for public methods, but ValueTask for internal methods that we expect to be mostly sync. 
    internal class EvalVisitor : IRNodeVisitor<ValueTask<FormulaValue>, EvalVisitorContext>
    {
        private readonly ReadOnlySymbolValues _symbolValues;

        private readonly CancellationToken _cancellationToken;

        public CancellationToken CancellationToken => _cancellationToken;

        private readonly IServiceProvider _services;

        public IServiceProvider FunctionServices => _services;

        public CultureInfo CultureInfo { get; private set; }

        public TimeZoneInfo TimeZoneInfo { get; private set; }

        public DateTimeKind DateTimeKind => TimeZoneInfo.Equals(TimeZoneInfo.Utc) ? DateTimeKind.Utc : DateTimeKind.Unspecified;

        public Governor Governor { get; private set; }

        private readonly Stack<UDFStackFrame> _udfStack = new Stack<UDFStackFrame>();

        public EvalVisitor(IRuntimeConfig config, CancellationToken cancellationToken)
        {
            _symbolValues = config.Values; // may be null 
            _cancellationToken = cancellationToken;
            _services = config.ServiceProvider ?? new BasicServiceProvider();

            TimeZoneInfo = GetService<TimeZoneInfo>() ?? TimeZoneInfo.Local;
            Governor = GetService<Governor>() ?? new Governor();
            CultureInfo = GetService<CultureInfo>() ?? throw new ArgumentNullException("Missing CultureInfo");
        }

        /// <summary>
        /// Get a service from the <see cref="ReadOnlySymbolValues"/>. Returns null if not present.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T GetService<T>()
        {
            return (T)_services.GetService(typeof(T));
        }

        public bool TryGetService<T>(out T result)
        {
            result = GetService<T>();
            return result != null;
        }

        // Check this cooperatively - especially in any loop. 
        public void CheckCancel()
        {
            // Throws OperationCanceledException exception
            _cancellationToken.ThrowIfCancellationRequested();

            Governor.Poll();
        }

        // Helper to eval an arg that might be a lambda.
        internal async ValueTask<DValue<T>> EvalArgAsync<T>(FormulaValue arg, EvalVisitorContext context, IRContext irContext)
            where T : ValidFormulaValue
        {
            if (arg is LambdaFormulaValue lambda)
            {
                arg = await lambda.EvalInRowScopeAsync(context).ConfigureAwait(false);
            }

            return arg switch
            {
                T t => DValue<T>.Of(t),
                BlankValue b => DValue<T>.Of(b),
                ErrorValue e => DValue<T>.Of(e),
                _ => DValue<T>.Of(CommonErrors.RuntimeTypeMismatch(irContext))
            };
        }

        public override async ValueTask<FormulaValue> Visit(TextLiteralNode node, EvalVisitorContext context)
        {
            return new StringValue(node.IRContext, node.LiteralValue);
        }

        public override async ValueTask<FormulaValue> Visit(NumberLiteralNode node, EvalVisitorContext context)
        {
            return new NumberValue(node.IRContext, node.LiteralValue);
        }

        public override async ValueTask<FormulaValue> Visit(DecimalLiteralNode node, EvalVisitorContext context)
        {
            return new DecimalValue(node.IRContext, node.LiteralValue);
        }

        public override async ValueTask<FormulaValue> Visit(BooleanLiteralNode node, EvalVisitorContext context)
        {
            return new BooleanValue(node.IRContext, node.LiteralValue);
        }

        public override async ValueTask<FormulaValue> Visit(ColorLiteralNode node, EvalVisitorContext context)
        {
            return new ColorValue(node.IRContext, node.LiteralValue);
        }

        public override async ValueTask<FormulaValue> Visit(RecordNode node, EvalVisitorContext context)
        {
            var fields = new List<NamedValue>();

            foreach (var field in node.Fields)
            {
                CheckCancel();

                var name = field.Key;
                var value = field.Value;

                var rhsValue = await value.Accept(this, context).ConfigureAwait(false);
                fields.Add(new NamedValue(name.Value, rhsValue));
            }

            return new InMemoryRecordValue(node.IRContext, fields);
        }

        public override async ValueTask<FormulaValue> Visit(LazyEvalNode node, EvalVisitorContext context)
        {
            var val = await node.Child.Accept(this, context).ConfigureAwait(false);
            return val;
        }

        // Handle the Set() function -
        // Set is unique because it has an l-value for the first arg. 
        // Async params can't have out-params. 
        // Return null if not handled. Else non-null if handled.
        private async Task<FormulaValue> TryHandleSet(CallNode node, EvalVisitorContext context)
        {
            // Special case Set() calls because they take an LValue. 
            if (node.Function.GetType() != typeof(RecalcEngineSetFunction))
            {
                return null;
            }

            var arg0 = node.Args[0];
            var arg1 = node.Args[1];

            var newValue = await arg1.Accept(this, context).ConfigureAwait(false);

            if (arg0.IRContext.IsMutation)
            {
                if (arg0 is RecordFieldAccessNode rfan)
                {
                    var arg0value = await rfan.From.Accept(this, context).ConfigureAwait(false);

                    if (arg0value is RecordValue rv)
                    {
                        rv.ShallowCopyFieldInPlace(rfan.Field);
                        rv.UpdateField(rfan.Field, newValue);
                        return node.IRContext.ResultType._type.Kind == DKind.Boolean ? FormulaValue.New(true) : FormulaValue.NewVoid();
                    }
                    else
                    {
                        return CommonErrors.UnreachableCodeError(node.IRContext);
                    }
                }
                else if (arg0 is BinaryOpNode bon && bon.Op == BinaryOpKind.DynamicGetField)
                {
                    var arg0value = await bon.Left.Accept(this, context).ConfigureAwait(false);
                    var arg1value = await bon.Right.Accept(this, context).ConfigureAwait(false);

                    if (arg0value is UntypedObjectValue uov && uov.Impl is UntypedObjectBase impl)
                    {
                        return impl.SetUntypedObject(node.IRContext, (StringValue)arg1value, newValue);
                    }
                    else if (arg0value is ErrorValue || arg0value is BlankValue)
                    {
                        return arg0value;
                    }
                }
                else if (arg0 is CallNode callNode && callNode.Function == BuiltinFunctionsCore.Index_UO)
                {
                    var child0Value = await callNode.Args[0].Accept(this, context).ConfigureAwait(false);
                    var child1Value = await callNode.Args[1].Accept(this, context).ConfigureAwait(false);

                    if (child0Value is UntypedObjectValue uov && uov.Impl is UntypedObjectBase impl)
                    {
                        return impl.SetUntypedObject(node.IRContext, (NumberValue)child1Value, newValue);
                    }
                    else if (child0Value is ErrorValue || child0Value is BlankValue)
                    {
                        return child0Value;
                    }
                }
                else
                {
                    return CommonErrors.UnreachableCodeError(node.IRContext);
                }
            }

            // Binder has already ensured this is a first name node as well as mutable symbol. 
            if (arg0 is ResolvedObjectNode obj)
            {
                if (obj.Value is ISymbolSlot sym)
                {
                    if (_symbolValues != null)
                    {
                        _symbolValues.Set(sym, newValue);
                        return node.IRContext.ResultType._type.Kind == DKind.Boolean ? FormulaValue.New(true) : FormulaValue.NewVoid();
                    }

                    // This may happen if the runtime symbols are missing a value and we failed to update. 
                }
            }

            // Fail?
            return CommonErrors.UnreachableCodeError(node.IRContext);
        }

        // Handle invoke SetProperty(source.Prop, newValue)
        // Invoke as: SetProperty(source, "Prop", newValue)
        private async Task<FormulaValue> TryHandleSetProperty(CallNode node, EvalVisitorContext context)
        {
            if (node.Function is not CustomSetPropertyFunction setPropFunc)
            {
                return null;
            }

            var arg0 = node.Args[0];
            var arg1 = node.Args[1];

            if (arg0 is not RecordFieldAccessNode r)
            {
                return null;
            }

            var source = await r.From.Accept(this, context).ConfigureAwait(false);
            var fieldName = r.Field.Value;
            var newValue = await arg1.Accept(this, context).ConfigureAwait(false);

            var args = new FormulaValue[] { source, FormulaValue.New(fieldName), newValue };
            var result = await setPropFunc.InvokeAsync(args, _cancellationToken).ConfigureAwait(false);

            return result;
        }

        // Given a TexlFunction, get the implementation to invoke. 
        private IFunctionInvoker GetInvoker(TexlFunction func)
        {
            if (func is IFunctionInvoker invoker)
            {
                return invoker;
            }

            if (func is UserDefinedFunction userDefinedFunc)
            {
                return new UserDefinedFunctionAdapter(userDefinedFunc);
            }

            if (FunctionImplementations.TryGetValue(func, out AsyncFunctionPtr ptr))
            {
                return new AsyncFunctionPtrAdapter(ptr);
            }

            return null;
        }

        // Adapter for AsyncFunctionPtr to common invoker interface.
        private class AsyncFunctionPtrAdapter : IFunctionInvoker
        {
            private readonly AsyncFunctionPtr _ptr;

            public AsyncFunctionPtrAdapter(AsyncFunctionPtr ptr)
            {
                _ptr = ptr;
            }

            public async Task<FormulaValue> InvokeAsync(FunctionInvokeInfo invokeInfo, CancellationToken cancellationToken)
            {
                var args = invokeInfo.Args.ToArray();
                var context = invokeInfo.Context;
                var evalVisitor = invokeInfo.Runner;
                var irContext = invokeInfo.IRContext;

                var result = await _ptr(evalVisitor, context, irContext, args).ConfigureAwait(false);

                return result;
            }
        }

        // Adapter for UDF to common invoker. 
        // This still ensures that *invoking* a UDF has the same semantics as invoking other function calls. 
        private class UserDefinedFunctionAdapter : IFunctionInvoker
        {
            private readonly UserDefinedFunction _udf;

            public UserDefinedFunctionAdapter(UserDefinedFunction udf)
            {
                _udf = udf;
            }

            public async Task<FormulaValue> InvokeAsync(FunctionInvokeInfo invokeInfo, CancellationToken cancellationToken)
            {
                var args = invokeInfo.Args.ToArray();
                var context = invokeInfo.Context;
                var evalVisitor = invokeInfo.Runner;

                var udfStack = evalVisitor._udfStack;

                UDFStackFrame frame = new UDFStackFrame(_udf, args);
                UDFStackFrame framePop = null;
                FormulaValue result = null;

                try
                {
                    // Push this so that we have access to args. 
                    udfStack.Push(frame);

                    // https://github.com/microsoft/Power-Fx/issues/2822
                    // This repeats IRTranslator each time. Do once and save. 
                    (var irnode, _) = _udf.GetIRTranslator();

                    evalVisitor.CheckCancel();

                    result = await irnode.Accept(evalVisitor, context).ConfigureAwait(false);
                }
                finally
                {
                    framePop = udfStack.Pop();
                }

                if (frame != framePop)
                {
                    throw new Exception("Something went wrong. UDF stack values didn't match.");
                }

                return result;
            }
        }

        public override async ValueTask<FormulaValue> Visit(CallNode node, EvalVisitorContext context)
        {
            CheckCancel();

            var setResult = await TryHandleSet(node, context.IncrementStackDepthCounter()).ConfigureAwait(false);
            if (setResult != null)
            {
                return setResult;
            }

            var setPropResult = await TryHandleSetProperty(node, context.IncrementStackDepthCounter()).ConfigureAwait(false);
            if (setPropResult != null)
            {
                return setPropResult;
            }

            var func = node.Function;

            var carg = node.Args.Count;

            var args = new FormulaValue[carg];

            for (var i = 0; i < carg; i++)
            {
                CheckCancel();

                var child = node.Args[i];
                var isLambda = node.IsLambdaArg(i);

                if (!isLambda)
                {
                    args[i] = await child.Accept(this, context.IncrementStackDepthCounter()).ConfigureAwait(false);
                }
                else
                {
                    // This is where Lambdas are created. They close over key values to invoke.
                    args[i] = new LambdaFormulaValue(node.IRContext, child, this, context);
                }
            }

            var childContext = context.SymbolContext.WithScope(node.Scope);

            FormulaValue result;

            // Remove this: https://github.com/microsoft/Power-Fx/issues/2821
            IReadOnlyDictionary<TexlFunction, IAsyncTexlFunction> extraFunctions = _services.GetService<IReadOnlyDictionary<TexlFunction, IAsyncTexlFunction>>();

            try
            {
                IFunctionInvoker invoker = GetInvoker(func);

                // Standard invoke path. Make everything go through here. 
                // Eventually collapse all cases to this. 
                if (invoker != null)
                {
                    var invokeInfo = new FunctionInvokeInfo
                    {
                        Args = args,
                        FunctionServices = _services,
                        Runner = this,
                        Context = context.IncrementStackDepthCounter(childContext),
                        IRContext = node.IRContext,
                    };

                    result = await invoker.InvokeAsync(invokeInfo, _cancellationToken).ConfigureAwait(false);
                }
                else if (func is IAsyncTexlFunction asyncFunc)
                {
                    result = await asyncFunc.InvokeAsync(args, _cancellationToken).ConfigureAwait(false);
                }
                else if (extraFunctions?.TryGetValue(func, out asyncFunc) == true)
                {
                    result = await asyncFunc.InvokeAsync(args, _cancellationToken).ConfigureAwait(false);
                }
                else if (func is IAsyncTexlFunction4 asyncFunc4)
                {
                    // https://github.com/microsoft/Power-Fx/issues/2818
                    // This is used for Json() functions.  IsType, AsType
                    result = await asyncFunc4.InvokeAsync(TimeZoneInfo, node.IRContext.ResultType, args, _cancellationToken).ConfigureAwait(false);
                }
                else if (func is IAsyncTexlFunction5 asyncFunc5)
                {
                    // https://github.com/microsoft/Power-Fx/issues/2818
                    // This is used for Json() functions.
                    BasicServiceProvider services2 = new BasicServiceProvider(_services);

                    // Invocation should not get its own provider.  
                    if (services2.GetService(typeof(TimeZoneInfo)) == null)
                    {
                        services2.AddService(TimeZoneInfo);
                    }

                    if (services2.GetService(typeof(Canceller)) == null)
                    {
                        services2.AddService(new Canceller(CheckCancel));
                    }

                    result = await asyncFunc5.InvokeAsync(services2, node.IRContext.ResultType, args, _cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = CommonErrors.NotYetImplementedFunctionError(node.IRContext, func.Name);
                }

                // https://github.com/microsoft/Power-Fx/issues/2820
                // We should remove this check that limits to just Adapter1, so we apply this check to all impls. 
                if (invoker is AsyncFunctionPtrAdapter) 
                {
                    if (!(result.IRContext.ResultType._type == node.IRContext.ResultType._type || result is ErrorValue || result.IRContext.ResultType is BlankType))
                    {
                        throw CommonExceptions.RuntimeMisMatch;
                    }
                }
            }
            catch (CustomFunctionErrorException ex)
            {
                var irContext = node.IRContext;
                result = new ErrorValue(irContext, new ExpressionError() { Message = ex.Message, Span = irContext.SourceContext, Kind = ex.ErrorKind });
            }

            CheckCancel();
            return result;
        }

        public override async ValueTask<FormulaValue> Visit(BinaryOpNode node, EvalVisitorContext context)
        {
            var arg1 = await node.Left.Accept(this, context).ConfigureAwait(false);
            var arg2 = await node.Right.Accept(this, context).ConfigureAwait(false);
            var args = new FormulaValue[] { arg1, arg2 };
            return await VisitBinaryOpNode(node, context, args).ConfigureAwait(false);
        }

        private ValueTask<FormulaValue> VisitBinaryOpNode(BinaryOpNode node, EvalVisitorContext context, FormulaValue[] args)
        {
            switch (node.Op)
            {
                case BinaryOpKind.AddNumbers:
                    return OperatorBinaryAdd(this, context, node.IRContext, args);
                case BinaryOpKind.AddDecimals:
                    return OperatorDecimalBinaryAdd(this, context, node.IRContext, args);
                case BinaryOpKind.MulNumbers:
                    return OperatorBinaryMul(this, context, node.IRContext, args);
                case BinaryOpKind.MulDecimals:
                    return OperatorDecimalBinaryMul(this, context, node.IRContext, args);
                case BinaryOpKind.DivNumbers:
                    return OperatorBinaryDiv(this, context, node.IRContext, args);
                case BinaryOpKind.DivDecimals:
                    return OperatorDecimalBinaryDiv(this, context, node.IRContext, args);

                case BinaryOpKind.EqBlob:

                case BinaryOpKind.EqBoolean:
                case BinaryOpKind.EqColor:
                case BinaryOpKind.EqCurrency:
                case BinaryOpKind.EqDate:
                case BinaryOpKind.EqDateTime:
                case BinaryOpKind.EqGuid:
                case BinaryOpKind.EqHyperlink:
                case BinaryOpKind.EqImage:
                case BinaryOpKind.EqMedia:
                case BinaryOpKind.EqNumbers:
                case BinaryOpKind.EqOptionSetValue:
                case BinaryOpKind.EqText:
                case BinaryOpKind.EqTime:
                case BinaryOpKind.EqNull:
                case BinaryOpKind.EqDecimals:
                    return OperatorBinaryEq(this, context, node.IRContext, args);
                case BinaryOpKind.EqNullUntyped:
                    return OperatorBinaryEqNullUntyped(this, context, node.IRContext, args);
                case BinaryOpKind.EqPolymorphic:
                    return OperatorBinaryEqPolymorphic(this, context, node.IRContext, args);
                case BinaryOpKind.NeqBlob:
                case BinaryOpKind.NeqBoolean:
                case BinaryOpKind.NeqColor:
                case BinaryOpKind.NeqCurrency:
                case BinaryOpKind.NeqDate:
                case BinaryOpKind.NeqDateTime:
                case BinaryOpKind.NeqGuid:
                case BinaryOpKind.NeqHyperlink:
                case BinaryOpKind.NeqImage:
                case BinaryOpKind.NeqMedia:
                case BinaryOpKind.NeqNumbers:
                case BinaryOpKind.NeqOptionSetValue:
                case BinaryOpKind.NeqText:
                case BinaryOpKind.NeqTime:
                case BinaryOpKind.NeqNull:
                case BinaryOpKind.NeqDecimals:
                    return OperatorBinaryNeq(this, context, node.IRContext, args);
                case BinaryOpKind.NeqNullUntyped:
                    return OperatorBinaryNeqNullUntyped(this, context, node.IRContext, args);
                case BinaryOpKind.NeqPolymorphic:
                    return OperatorBinaryNeqPolymorphic(this, context, node.IRContext, args);

                case BinaryOpKind.GtNumbers:
                case BinaryOpKind.GtNull:
                    return OperatorBinaryGt(this, context, node.IRContext, args);
                case BinaryOpKind.GeqNumbers:
                case BinaryOpKind.GeqNull:
                    return OperatorBinaryGeq(this, context, node.IRContext, args);
                case BinaryOpKind.LtNumbers:
                case BinaryOpKind.LtNull:
                    return OperatorBinaryLt(this, context, node.IRContext, args);
                case BinaryOpKind.LeqNumbers:
                case BinaryOpKind.LeqNull:
                    return OperatorBinaryLeq(this, context, node.IRContext, args);

                case BinaryOpKind.GtDecimals:
                    return OperatorDecimalBinaryGt(this, context, node.IRContext, args);
                case BinaryOpKind.GeqDecimals:
                    return OperatorDecimalBinaryGeq(this, context, node.IRContext, args);
                case BinaryOpKind.LtDecimals:
                    return OperatorDecimalBinaryLt(this, context, node.IRContext, args);
                case BinaryOpKind.LeqDecimals:
                    return OperatorDecimalBinaryLeq(this, context, node.IRContext, args);

                case BinaryOpKind.InText:
                    return OperatorTextIn(this, context, node.IRContext, args);
                case BinaryOpKind.ExactInText:
                    return OperatorTextInExact(this, context, node.IRContext, args);

                case BinaryOpKind.InScalarTable:
                    return OperatorScalarTableIn(this, context, node.IRContext, args);

                case BinaryOpKind.ExactInScalarTable:
                    return OperatorScalarTableInExact(this, context, node.IRContext, args);

                case BinaryOpKind.AddDateAndTime:
                    return OperatorAddDateAndTime(this, context, node.IRContext, args);
                case BinaryOpKind.AddDateAndDay:
                    return OperatorAddDateAndDay(this, context, node.IRContext, args);
                case BinaryOpKind.AddDateTimeAndDay:
                    return OperatorAddDateTimeAndDay(this, context, node.IRContext, args);
                case BinaryOpKind.AddTimeAndNumber:
                    return OperatorAddTimeAndNumber(this, context, node.IRContext, args);
                case BinaryOpKind.AddNumberAndTime:
                    return OperatorAddTimeAndNumber(this, context, node.IRContext, new[] { args[1], args[0] });
                case BinaryOpKind.AddTimeAndTime:
                    return OperatorAddTimeAndTime(this, context, node.IRContext, args);
                case BinaryOpKind.DateDifference:
                    return OperatorDateDifference(this, context, node.IRContext, args);
                case BinaryOpKind.TimeDifference:
                    return OperatorTimeDifference(this, context, node.IRContext, args);
                case BinaryOpKind.SubtractDateAndTime:
                    return OperatorSubtractDateAndTime(this, context, node.IRContext, args);
                case BinaryOpKind.SubtractNumberAndDate:
                    return OperatorSubtractNumberAndDate(this, context, node.IRContext, args);
                case BinaryOpKind.SubtractNumberAndTime:
                    return OperatorSubtractNumberAndTime(this, context, node.IRContext, args);
                case BinaryOpKind.LtDateTime:
                    return OperatorLtDateTime(this, context, node.IRContext, args);
                case BinaryOpKind.LeqDateTime:
                    return OperatorLeqDateTime(this, context, node.IRContext, args);
                case BinaryOpKind.GtDateTime:
                    return OperatorGtDateTime(this, context, node.IRContext, args);
                case BinaryOpKind.GeqDateTime:
                    return OperatorGeqDateTime(this, context, node.IRContext, args);
                case BinaryOpKind.LtDate:
                    return OperatorLtDate(this, context, node.IRContext, args);
                case BinaryOpKind.LeqDate:
                    return OperatorLeqDate(this, context, node.IRContext, args);
                case BinaryOpKind.GtDate:
                    return OperatorGtDate(this, context, node.IRContext, args);
                case BinaryOpKind.GeqDate:
                    return OperatorGeqDate(this, context, node.IRContext, args);
                case BinaryOpKind.LtTime:
                    return OperatorLtTime(this, context, node.IRContext, args);
                case BinaryOpKind.LeqTime:
                    return OperatorLeqTime(this, context, node.IRContext, args);
                case BinaryOpKind.GtTime:
                    return OperatorGtTime(this, context, node.IRContext, args);
                case BinaryOpKind.GeqTime:
                    return OperatorGeqTime(this, context, node.IRContext, args);
                case BinaryOpKind.DynamicGetField:
                    return new ValueTask<FormulaValue>(OperatorDynamicGetField(node, args));

                default:
                    return new ValueTask<FormulaValue>(CommonErrors.UnreachableCodeError(node.IRContext));
            }
        }

        private static FormulaValue OperatorDynamicGetField(BinaryOpNode node, FormulaValue[] args)
        {
            var arg1 = args[0];
            var arg2 = args[1];

            if (arg1 is UntypedObjectValue cov && arg2 is StringValue sv)
            {
                if (cov.Impl.Type is ExternalType et && (et.Kind == ExternalTypeKind.Object || et.Kind == ExternalTypeKind.ArrayAndObject))
                {
                    if (cov.Impl is UntypedObjectBase untypedObjectBase)
                    {
                        return untypedObjectBase.GetProperty(sv.Value, node.IRContext.ResultType);
                    }

                    if (cov.Impl.TryGetProperty(sv.Value, out var res))
                    {
                        if (res.Type == FormulaType.Blank)
                        {
                            return new BlankValue(node.IRContext);
                        }

                        return new UntypedObjectValue(node.IRContext, res);
                    }
                    else
                    {
                        return new BlankValue(node.IRContext);
                    }
                }
                else if (cov.Impl.Type == FormulaType.Blank)
                {
                    return new BlankValue(node.IRContext);
                }
                else
                {
                    return new ErrorValue(node.IRContext, new ExpressionError()
                    {
                        ResourceKey = RuntimeStringResources.ErrAccessingFieldNotValidValue,
                        Span = node.IRContext.SourceContext,
                        Kind = ErrorKind.InvalidArgument
                    });
                }
            }
            else if (arg1 is BlankValue)
            {
                return new BlankValue(node.IRContext);
            }
            else if (arg1 is ErrorValue)
            {
                return arg1;
            }
            else
            {
                return CommonErrors.UnreachableCodeError(node.IRContext);
            }
        }

        public override async ValueTask<FormulaValue> Visit(UnaryOpNode node, EvalVisitorContext context)
        {
            var arg1 = await node.Child.Accept(this, context).ConfigureAwait(false);
            var args = new FormulaValue[] { arg1 };

            if (UnaryOps.TryGetValue(node.Op, out var unaryOp))
            {
                return await unaryOp(this, context, node.IRContext, args).ConfigureAwait(false);
            }

            return CommonErrors.NotYetImplementedUnaryOperatorError(node.IRContext, node.Op.ToString());
        }

        public override async ValueTask<FormulaValue> Visit(AggregateCoercionNode node, EvalVisitorContext context)
        {
            var arg1 = await node.Child.Accept(this, context).ConfigureAwait(false);

            if (arg1 is BlankValue || arg1 is ErrorValue)
            {
                return arg1;
            }

            if (node.Op == UnaryOpKind.TableToTable)
            {
                var table = (TableValue)arg1;
                var tableType = (TableType)node.IRContext.ResultType;
                var resultRows = new List<DValue<RecordValue>>();
                foreach (var row in table.Rows)
                {
                    CheckCancel();

                    if (row.IsValue)
                    {
                        var fields = new List<NamedValue>();
                        var scopeContext = context.SymbolContext.WithScope(node.Scope);

                        foreach (var field in row.Value.Fields)
                        {
                            CheckCancel();

                            if (node.FieldCoercions.TryGetValue(new Core.Utils.DName(field.Name), out var coercion))
                            {
                                var record = row.Value;
                                var newScope = scopeContext.WithScopeValues(record);
                                var newValue = await coercion.Accept(this, context.NewScope(newScope)).ConfigureAwait(false);

                                fields.Add(new NamedValue(field.Name, newValue));
                            }
                            else
                            {
                                fields.Add(field);
                            }
                        }

                        resultRows.Add(DValue<RecordValue>.Of(new InMemoryRecordValue(IRContext.NotInSource(tableType.ToRecord()), fields)));
                    }
                    else if (row.IsBlank)
                    {
                        resultRows.Add(DValue<RecordValue>.Of(row.Blank));
                    }
                    else
                    {
                        resultRows.Add(DValue<RecordValue>.Of(row.Error));
                    }
                }

                return new InMemoryTableValue(node.IRContext, resultRows);
            }
            else if (node.Op == UnaryOpKind.RecordToRecord)
            {
                var fields = new List<NamedValue>();

                var scopeContext = context.SymbolContext.WithScope(node.Scope);
                var newScope = scopeContext.WithScopeValues((RecordValue)arg1);

                var recordSrc = (RecordValue)arg1;
                foreach (var f2 in recordSrc.Fields)
                {
                    CheckCancel();

                    if (node.FieldCoercions.TryGetValue(new Core.Utils.DName(f2.Name), out var coercion))
                    {
                        var newValue = await coercion.Accept(this, context.NewScope(newScope)).ConfigureAwait(false);

                        fields.Add(new NamedValue(f2.Name, newValue));
                    }
                    else
                    {
                        // Existing field, no coercion needed. 
                        fields.Add(f2);
                    }
                }

                return FormulaValue.NewRecordFromFields(fields);
            }

            return CommonErrors.UnreachableCodeError(node.IRContext);
        }

        public override async ValueTask<FormulaValue> Visit(ScopeAccessNode node, EvalVisitorContext context)
        {
            if (node.Value is ScopeAccessSymbol s1)
            {
                var scope = s1.Parent;

                var val = context.SymbolContext.GetScopeVar(scope, s1.Name);
                return val;
            }

            // Binds to whole scope
            if (node.Value is ScopeSymbol s2)
            {
                var r = context.SymbolContext.ScopeValues[s2.Id];
                return r.Resolve(string.Empty);
            }

            return CommonErrors.UnreachableCodeError(node.IRContext);
        }

        public override async ValueTask<FormulaValue> Visit(RecordFieldAccessNode node, EvalVisitorContext context)
        {
            var left = await node.From.Accept(this, context).ConfigureAwait(false);

            if (left is BlankValue)
            {
                return new BlankValue(node.IRContext);
            }

            if (left is ErrorValue)
            {
                return left;
            }

            var record = (RecordValue)left;

            if (node.IRContext.IsMutation)
            {
                record.ShallowCopyFieldInPlace(node.Field.Value);
            }

            var val = await record.GetFieldAsync(node.IRContext.ResultType, node.Field.Value, _cancellationToken).ConfigureAwait(false);

            return val;
        }

        public override async ValueTask<FormulaValue> Visit(SingleColumnTableAccessNode node, EvalVisitorContext context)
        {
            return new ErrorValue(node.IRContext, new ExpressionError()
            {
                ResourceKey = RuntimeStringResources.ErrSingleColumnTableAccessNodeNotYetImplemented,
                Span = node.IRContext.SourceContext,
                Kind = ErrorKind.NotSupported
            });
        }

        public override async ValueTask<FormulaValue> Visit(ErrorNode node, EvalVisitorContext context)
        {
            return new ErrorValue(node.IRContext, new ExpressionError()
            {
                Message = node.ErrorHint,
                Span = node.IRContext.SourceContext,
                Kind = ErrorKind.AnalysisError
            });
        }

        public override async ValueTask<FormulaValue> Visit(ChainingNode node, EvalVisitorContext context)
        {
            CheckCancel();

            if (!node.Nodes.Any())
            {
                return CommonErrors.InvalidChain(node.IRContext, node.ToString());
            }

            FormulaValue fv = null;
            var errors = new List<ExpressionError>();

            foreach (var iNode in node.Nodes)
            {
                CheckCancel();

                fv = await iNode.Accept(this, context).ConfigureAwait(false);

                if (fv is ErrorValue ev)
                {
                    errors.AddRange(ev.Errors);
                }
            }

            return errors.Any() ? new ErrorValue(node.IRContext, errors) : fv;
        }

        public override async ValueTask<FormulaValue> Visit(ResolvedObjectNode node, EvalVisitorContext context)
        {
            switch (node.Value)
            {
                case NameSymbol name:
                    return GetVariableOrFail(node, name, node.IRContext.IsMutation);
                case FormulaValue fi:
                    return fi;
                case IExternalOptionSet optionSet:
                    return ResolvedObjectHelpers.OptionSet(optionSet, node.IRContext);
                case Func<IServiceProvider, Task<FormulaValue>> getHostObject:
                    FormulaValue hostObj;
                    try
                    {
                        hostObj = await getHostObject(_services).ConfigureAwait(false);
                        if (!hostObj.Type._type.Accepts(node.IRContext.ResultType._type, exact: true, useLegacyDateTimeAccepts: false, usePowerFxV1CompatibilityRules: true))
                        {
                            hostObj = CommonErrors.RuntimeTypeMismatch(node.IRContext);
                        }
                    }
                    catch (CustomFunctionErrorException ex)
                    {
                        hostObj = CommonErrors.RuntimeExceptionError(node.IRContext, ex.Message);
                    }

                    return hostObj;
                case UDFParameterInfo uDFParameterInfo:
                    var frame = _udfStack.Peek();
                    return frame.GetArg(uDFParameterInfo);
                default:
                    return ResolvedObjectHelpers.ResolvedObjectError(node);
            }
        }

        private FormulaValue GetVariableOrFail(ResolvedObjectNode node, ISymbolSlot slot, bool isMutation = false)
        {
            if (_symbolValues != null)
            {
                var value = _symbolValues.Get(slot);
                if (value != null)
                {
                    if (isMutation)
                    {
                        value = value.MaybeShallowCopy();
                        _symbolValues.Set(slot, value);
                    }

                    return value;
                }
            }

            return ResolvedObjectHelpers.ResolvedObjectError(node);
        }

        public DateTime GetNormalizedDateTime(FormulaValue arg)
        {
            return GetNormalizedDateTimeLibrary(arg, TimeZoneInfo);
        }

        public DateTime GetNormalizedDateTimeAllowTimeValue(FormulaValue arg)
        {
            switch (arg)
            {
                case DateTimeValue dtv:
                    return dtv.GetConvertedValue(TimeZoneInfo);
                case DateValue dv:
                    return dv.GetConvertedValue(TimeZoneInfo);
                case TimeValue tv:
                    return _epoch.Add(tv.Value);
                default:
                    throw CommonExceptions.RuntimeMisMatch;
            }
        }

        public TimeSpan GetNormalizedTimeSpan(FormulaValue arg)
        {
            switch (arg)
            {
                case DateTimeValue dtv:
                    return dtv.GetConvertedValue(TimeZoneInfo).TimeOfDay;
                case TimeValue dv:
                    return dv.Value;
                default:
                    throw CommonExceptions.RuntimeMisMatch;
            }
        }

        public TimeSpan GetNormalizedTimeSpanWithoutDay(FormulaValue arg)
        {
            switch (arg)
            {
                case DateTimeValue dtv:
                    var dtvValue = dtv.GetConvertedValue(TimeZoneInfo);
                    return new TimeSpan(0, dtvValue.Hour, dtvValue.Minute, dtvValue.Second, dtvValue.Millisecond);
                case TimeValue tv:
                    return tv.Value;
                default:
                    throw CommonExceptions.RuntimeMisMatch;
            }
        }
    }
}
