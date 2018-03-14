﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NLog;
using Torch.Managers.PatchManager.MSIL;
using Torch.Managers.PatchManager.Transpile;
using Torch.Utils;

namespace Torch.Managers.PatchManager
{
    internal class DecoratedMethod : MethodRewritePattern
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        private readonly MethodBase _method;

        internal DecoratedMethod(MethodBase method) : base(null)
        {
            _method = method;
        }

        private long _revertAddress;
        private byte[] _revertData = null;
        private GCHandle? _pinnedPatch;

        internal bool HasChanged()
        {
            return Prefixes.HasChanges() || Suffixes.HasChanges() || Transpilers.HasChanges() || PostTranspilers.HasChanges();
        }

        internal void Commit()
        {
            try
            {
                // non-greedy so they are all reset
                if (!Prefixes.HasChanges(true) & !Suffixes.HasChanges(true) & !Transpilers.HasChanges(true) & !PostTranspilers.HasChanges(true))
                    return;
                Revert();

                if (Prefixes.Count == 0 && Suffixes.Count == 0 && Transpilers.Count == 0 && PostTranspilers.Count == 0)
                    return;
                _log.Log(PrintMsil ? LogLevel.Info : LogLevel.Debug,
                    $"Begin patching {_method.DeclaringType?.FullName}#{_method.Name}({string.Join(", ", _method.GetParameters().Select(x => x.ParameterType.Name))})");
                var patch = ComposePatchedMethod();

                _revertAddress = AssemblyMemory.GetMethodBodyStart(_method);
                var newAddress = AssemblyMemory.GetMethodBodyStart(patch);
                _revertData = AssemblyMemory.WriteJump(_revertAddress, newAddress);
                _pinnedPatch = GCHandle.Alloc(patch);
                _log.Log(PrintMsil ? LogLevel.Info : LogLevel.Debug,
                    $"Done patching {_method.DeclaringType?.FullName}#{_method.Name}({string.Join(", ", _method.GetParameters().Select(x => x.ParameterType.Name))})");
            }
            catch (Exception exception)
            {
                _log.Fatal(exception, $"Error patching {_method.DeclaringType?.FullName}#{_method}");
                throw;
            }
        }

        internal void Revert()
        {
            if (_pinnedPatch.HasValue)
            {
                _log.Debug($"Revert {_method.DeclaringType?.FullName}#{_method.Name}({string.Join(", ", _method.GetParameters().Select(x => x.ParameterType.Name))})");
                AssemblyMemory.WriteMemory(_revertAddress, _revertData);
                _revertData = null;
                _pinnedPatch.Value.Free();
                _pinnedPatch = null;
            }
        }

        #region Create
        private int _patchSalt = 0;
        private DynamicMethod AllocatePatchMethod()
        {
            Debug.Assert(_method.DeclaringType != null);
            var methodName = _method.Name + $"_{_patchSalt++}";
            var returnType = _method is MethodInfo meth ? meth.ReturnType : typeof(void);
            var parameters = _method.GetParameters();
            var parameterTypes = (_method.IsStatic ? Enumerable.Empty<Type>() : new[] { typeof(object) })
                .Concat(parameters.Select(x => x.ParameterType)).ToArray();

            var patchMethod = new DynamicMethod(methodName, MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard,
                returnType, parameterTypes, _method.DeclaringType, true);
            if (!_method.IsStatic)
                patchMethod.DefineParameter(0, ParameterAttributes.None, INSTANCE_PARAMETER);
            for (var i = 0; i < parameters.Length; i++)
                patchMethod.DefineParameter((patchMethod.IsStatic ? 0 : 1) + i, parameters[i].Attributes, parameters[i].Name);

            return patchMethod;
        }


        public const string INSTANCE_PARAMETER = "__instance";
        public const string RESULT_PARAMETER = "__result";
        public const string PREFIX_SKIPPED_PARAMETER = "__prefixSkipped";


        public DynamicMethod ComposePatchedMethod()
        {
            DynamicMethod method = AllocatePatchMethod();
            var generator = new LoggingIlGenerator(method.GetILGenerator(), PrintMsil ? LogLevel.Info : LogLevel.Trace);
            List<MsilInstruction> il = EmitPatched((type, pinned) => new MsilLocal(generator.DeclareLocal(type, pinned))).ToList();
            if (PrintMsil)
            {
                lock (_log)
                {
                    MethodTranspiler.IntegrityAnalysis(LogLevel.Info, il);
                }
            }
            MethodTranspiler.EmitMethod(il, generator);

            try
            {
                PatchUtilities.Compile(method);
            }
            catch
            {
                lock (_log)
                {
                    var ctx = new MethodContext(method);
                    ctx.Read();
                    MethodTranspiler.IntegrityAnalysis(LogLevel.Warn, ctx.Instructions);
                }
                throw;
            }
            return method;
        }
        #endregion

        #region Emit
        private IEnumerable<MsilInstruction> EmitPatched(Func<Type, bool, MsilLocal> declareLocal)
        {
            var methodBody = _method.GetMethodBody();
            Debug.Assert(methodBody != null, "Method body is null");
            foreach (var localVar in methodBody.LocalVariables)
            {
                Debug.Assert(localVar.LocalType != null);
                declareLocal(localVar.LocalType, localVar.IsPinned);
            }
            var instructions = new List<MsilInstruction>();
            var specialVariables = new Dictionary<string, MsilLocal>();

            var labelAfterOriginalContent = new MsilLabel();
            var labelSkipMethodContent = new MsilLabel();


            Type returnType = _method is MethodInfo meth ? meth.ReturnType : typeof(void);
            MsilLocal resultVariable = null;
            if (returnType != typeof(void))
            {
                if (Prefixes.Concat(Suffixes).SelectMany(x => x.GetParameters()).Any(x => x.Name == RESULT_PARAMETER)
                    || Prefixes.Any(x => x.ReturnType == typeof(bool)))
                    resultVariable = declareLocal(returnType, false);
            }
            if (resultVariable != null)
                instructions.AddRange(resultVariable.SetToDefault());
            MsilLocal prefixSkippedVariable = null;
            if (Prefixes.Count > 0 && Suffixes.Any(x => x.GetParameters()
                    .Any(y => y.Name.Equals(PREFIX_SKIPPED_PARAMETER))))
            {
                prefixSkippedVariable = declareLocal(typeof(bool), false);
                specialVariables.Add(PREFIX_SKIPPED_PARAMETER, prefixSkippedVariable);
            }

            if (resultVariable != null)
                specialVariables.Add(RESULT_PARAMETER, resultVariable);

            foreach (MethodInfo prefix in Prefixes)
            {
                instructions.AddRange(EmitMonkeyCall(prefix, specialVariables));
                if (prefix.ReturnType == typeof(bool))
                    instructions.Add(new MsilInstruction(OpCodes.Brfalse).InlineTarget(labelSkipMethodContent));
                else if (prefix.ReturnType != typeof(void))
                    throw new Exception(
                        $"Prefixes must return void or bool.  {prefix.DeclaringType?.FullName}.{prefix.Name} returns {prefix.ReturnType}");
            }
            instructions.AddRange(MethodTranspiler.Transpile(_method, (x) => declareLocal(x, false), Transpilers, labelAfterOriginalContent));

            instructions.Add(new MsilInstruction(OpCodes.Nop).LabelWith(labelAfterOriginalContent));
            if (resultVariable != null)
                instructions.Add(new MsilInstruction(OpCodes.Stloc).InlineValue(resultVariable));
            var notSkip = new MsilLabel();
            instructions.Add(new MsilInstruction(OpCodes.Br).InlineTarget(notSkip));
            instructions.Add(new MsilInstruction(OpCodes.Nop).LabelWith(labelSkipMethodContent));
            if (prefixSkippedVariable != null)
            {
                instructions.Add(new MsilInstruction(OpCodes.Ldc_I4_1));
                instructions.Add(new MsilInstruction(OpCodes.Stloc).InlineValue(prefixSkippedVariable));
            }
            instructions.Add(new MsilInstruction(OpCodes.Nop).LabelWith(notSkip));

            foreach (MethodInfo suffix in Suffixes)
            {
                instructions.AddRange(EmitMonkeyCall(suffix, specialVariables));
                if (suffix.ReturnType != typeof(void))
                    throw new Exception($"Suffixes must return void.  {suffix.DeclaringType?.FullName}.{suffix.Name} returns {suffix.ReturnType}");
            }
            if (resultVariable != null)
                instructions.Add(new MsilInstruction(OpCodes.Ldloc).InlineValue(resultVariable));
            instructions.Add(new MsilInstruction(OpCodes.Ret));

            var result = MethodTranspiler.Transpile(_method, instructions, (x) => declareLocal(x, false), PostTranspilers, null).ToList();
            if (result.Last().OpCode != OpCodes.Ret)
                result.Add(new MsilInstruction(OpCodes.Ret));
            return result;
        }

        private IEnumerable<MsilInstruction> EmitMonkeyCall(MethodInfo patch,
            IReadOnlyDictionary<string, MsilLocal> specialVariables)
        {
            foreach (var param in patch.GetParameters())
            {
                switch (param.Name)
                {
                    case INSTANCE_PARAMETER:
                        if (_method.IsStatic)
                            throw new Exception("Can't use an instance parameter for a static method");
                        yield return new MsilInstruction(OpCodes.Ldarg_0);
                        break;
                    case PREFIX_SKIPPED_PARAMETER:
                        if (param.ParameterType != typeof(bool))
                            throw new Exception($"Prefix skipped parameter {param.ParameterType} must be of type bool");
                        if (param.ParameterType.IsByRef || param.IsOut)
                            throw new Exception($"Prefix skipped parameter {param.ParameterType} can't be a reference type");
                        if (specialVariables.TryGetValue(PREFIX_SKIPPED_PARAMETER, out MsilLocal prefixSkip))
                            yield return new MsilInstruction(OpCodes.Ldloc).InlineValue(prefixSkip);
                        else
                            yield return new MsilInstruction(OpCodes.Ldc_I4_0);
                        break;
                    case RESULT_PARAMETER:
                        Type retType = param.ParameterType.IsByRef
                            ? param.ParameterType.GetElementType()
                            : param.ParameterType;
                        if (retType == null || !retType.IsAssignableFrom(specialVariables[RESULT_PARAMETER].Type))
                            throw new Exception($"Return type {specialVariables[RESULT_PARAMETER].Type} can't be assigned to result parameter type {retType}");
                        yield return new MsilInstruction(param.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc)
                            .InlineValue(specialVariables[RESULT_PARAMETER]);
                        break;
                    default:
                        ParameterInfo declParam = _method.GetParameters().FirstOrDefault(x => x.Name == param.Name);
                        if (declParam == null)
                            throw new Exception($"Parameter name {param.Name} not found");
                        int paramIdx = (_method.IsStatic ? 0 : 1) + declParam.Position;

                        bool patchByRef = param.IsOut || param.ParameterType.IsByRef;
                        bool declByRef = declParam.IsOut || declParam.ParameterType.IsByRef;
                        if (patchByRef == declByRef)
                            yield return new MsilInstruction(OpCodes.Ldarg).InlineValue(new MsilArgument(paramIdx));
                        else if (patchByRef)
                            yield return new MsilInstruction(OpCodes.Ldarga).InlineValue(new MsilArgument(paramIdx));
                        else
                        {
                            yield return new MsilInstruction(OpCodes.Ldarg).InlineValue(new MsilArgument(paramIdx));
                            yield return EmitExtensions.EmitDereference(declParam.ParameterType);
                        }
                        break;
                }
            }
            yield return new MsilInstruction(OpCodes.Call).InlineValue(patch);
        }
        #endregion
    }
}
