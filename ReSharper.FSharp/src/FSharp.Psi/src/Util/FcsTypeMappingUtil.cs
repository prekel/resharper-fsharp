using System;
using System.Collections.Generic;
using System.Linq;
using FSharp.Compiler.Symbols;
using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.Metadata.Reader.API;
using JetBrains.Metadata.Reader.Impl;
using JetBrains.ReSharper.Plugins.FSharp.Psi.Tree;
using JetBrains.ReSharper.Plugins.FSharp.Util;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace JetBrains.ReSharper.Plugins.FSharp.Psi.Util
{
  /// <summary>
  /// Map FSharpType elements (as seen by FSharp.Compiler.Service) to IType types.
  /// </summary>
  public static class FcsTypeMappingUtil
  {
    [CanBeNull]
    public static IDeclaredType MapBaseType([NotNull] this FSharpEntity entity, IList<ITypeParameter> typeParams,
      [NotNull] IPsiModule psiModule) =>
      entity.BaseType?.Value is { } baseType
        ? MapType(baseType, typeParams, psiModule) as IDeclaredType
        : TypeFactory.CreateUnknownType(psiModule);

    [NotNull]
    public static IEnumerable<IDeclaredType> GetSuperTypes([NotNull] this FSharpEntity entity,
      IList<ITypeParameter> typeParams, [NotNull] IPsiModule psiModule)
    {
      var interfaces = entity.DeclaredInterfaces;
      var types = new List<IDeclaredType>(interfaces.Count + 1);
      foreach (var entityInterface in interfaces)
        if (MapType(entityInterface, typeParams, psiModule) is IDeclaredType declaredType)
          types.Add(declaredType);

      var baseType = MapBaseType(entity, typeParams, psiModule);
      if (baseType != null)
        types.Add(baseType);

      return types;
    }

    [CanBeNull]
    public static IClrTypeName GetClrName([NotNull] this FSharpEntity entity)
    {
      if (entity.IsArrayType)
        return PredefinedType.ARRAY_FQN;

      try
      {
        return new ClrTypeName(entity.QualifiedBaseName);
      }
      catch (Exception e)
      {
        Logger.LogMessage(LoggingLevel.WARN, "Could not map FSharpEntity: {0}", entity);
        Logger.LogExceptionSilently(e);
        return null;
      }
    }

    private static bool HasGenericTypeParams([NotNull] FSharpType fsType)
    {
      if (fsType.IsGenericParameter)
        return true;

      foreach (var typeArg in fsType.GenericArguments)
        if (typeArg.IsGenericParameter || HasGenericTypeParams(typeArg))
          return true;

      return false;
    }

    [CanBeNull]
    private static FSharpType GetStrippedType([NotNull] FSharpType type)
    {
      try
      {
        return type.StrippedType;
      }
      catch (Exception e)
      {
        Logger.LogMessage(LoggingLevel.WARN, "Getting stripped type: {0}", type);
        Logger.LogExceptionSilently(e);
        return null;
      }
    }

    [NotNull]
    public static IType MapType([NotNull] this FSharpType fsType, [NotNull] IList<ITypeParameter> typeParams,
      [NotNull] IPsiModule psiModule, bool isFromMethod = false, bool isFromReturn = false)
    {
      var type = GetStrippedType(fsType);
      if (type == null || type.IsUnresolved)
        return TypeFactory.CreateUnknownType(psiModule);

      // F# 4.0 specs 18.1.3
      try
      {
        // todo: check type vs fsType
        if (isFromMethod && type.IsNativePtr && !HasGenericTypeParams(fsType))
        {
          var argType = GetSingleTypeArgument(fsType, typeParams, psiModule, true);
          return TypeFactory.CreatePointerType(argType);
        }
      }
      catch (Exception e)
      {
        Logger.LogMessage(LoggingLevel.WARN, "Could not map pointer type: {0}", fsType);
        Logger.LogExceptionSilently(e);
      }

      if (isFromReturn && type.IsUnit)
        return psiModule.GetPredefinedType().Void;

      if (type.IsGenericParameter)
        return GetTypeParameterByName(type, typeParams, psiModule);

      if (!type.HasTypeDefinition)
        return TypeFactory.CreateUnknownType(psiModule);

      var entity = type.TypeDefinition;
      // F# 4.0 specs 5.1.4
      if (entity.IsArrayType)
      {
        var argType = GetSingleTypeArgument(type, typeParams, psiModule, isFromMethod);
        return TypeFactory.CreateArrayType(argType, type.TypeDefinition.ArrayRank, NullableAnnotation.Unknown);
      }

      // e.g. byref<int>, we need int
      if (entity.IsByRef)
        return MapType(type.GenericArguments[0], typeParams, psiModule, isFromMethod, isFromReturn);

      if (entity.IsProvidedAndErased)
        return entity.BaseType is { } baseType
          ? MapType(baseType.Value, typeParams, psiModule, isFromMethod, isFromReturn)
          : TypeFactory.CreateUnknownType(psiModule);

      var clrName = entity.GetClrName();
      if (clrName == null)
      {
        // bug Microsoft/visualfsharp#3532
        // e.g. byref<int>, we need int
        return entity.CompiledName == "byref`1" && entity.AccessPath == "Microsoft.FSharp.Core"
          ? MapType(type.GenericArguments[0], typeParams, psiModule, isFromMethod, isFromReturn)
          : TypeFactory.CreateUnknownType(psiModule);
      }

      var declaredType = clrName.CreateTypeByClrName(psiModule);
      var genericArgs = type.GenericArguments;
      if (genericArgs.IsEmpty())
        return declaredType;

      var typeElement = declaredType.GetTypeElement();
      return typeElement != null
        ? GetTypeWithSubstitution(typeElement, genericArgs, typeParams, psiModule, isFromMethod)
        : TypeFactory.CreateUnknownType(psiModule);
    }

    public static IType MapType([NotNull] this FSharpType fsType, [NotNull] ITreeNode treeNode) =>
      // todo: get external type parameters
      MapType(fsType, EmptyList<ITypeParameter>.Instance, treeNode.GetPsiModule());

    [NotNull]
    private static IType GetSingleTypeArgument([NotNull] FSharpType fsType, IList<ITypeParameter> typeParams,
      IPsiModule psiModule, bool isFromMethod)
    {
      var genericArgs = fsType.GenericArguments;
      Assertion.Assert(genericArgs.Count == 1, "genericArgs.Count == 1");
      return GetTypeArgumentType(genericArgs[0], typeParams, psiModule, isFromMethod);
    }

    [NotNull]
    private static IDeclaredType GetTypeWithSubstitution([NotNull] ITypeElement typeElement,
      IList<FSharpType> fsTypeArgs, [NotNull] IList<ITypeParameter> typeParams, [NotNull] IPsiModule psiModule,
      bool isFromMethod)
    {
      var typeParamsCount = typeElement.GetAllTypeParameters().Count;
      var typeArgs = new IType[typeParamsCount];
      for (var i = 0; i < typeParamsCount; i++)
        typeArgs[i] = GetTypeArgumentType(fsTypeArgs[i], typeParams, psiModule, isFromMethod);

      return TypeFactory.CreateType(typeElement, typeArgs);
    }

    [NotNull]
    private static IType GetTypeArgumentType([NotNull] FSharpType arg, [NotNull] IList<ITypeParameter> typeParams,
      [NotNull] IPsiModule psiModule, bool isFromMethod) =>
      arg.IsGenericParameter
        ? GetTypeParameterByName(arg, typeParams, psiModule)
        : MapType(arg, typeParams, psiModule, isFromMethod);

    // todo: remove and add API to FCS.FSharpParameter
    [NotNull]
    private static IType GetTypeParameterByName([NotNull] FSharpType type,
      [NotNull] IList<ITypeParameter> typeParameters, [NotNull] IPsiModule psiModule)
    {
      var paramName = type.GenericParameter.Name;
      var typeParam = typeParameters.FirstOrDefault(p => p.ShortName == paramName);
      return typeParam != null
        ? TypeFactory.CreateType(typeParam)
        : TypeFactory.CreateUnknownType(psiModule);
    }

    public static ParameterKind MapParameterKind([NotNull] this FSharpParameter param)
    {
      var fsType = param.Type;
      if (fsType.HasTypeDefinition && fsType.TypeDefinition is var entity && entity.IsByRef)
      {
        if (param.IsOut || entity.LogicalName == "outref`1")
          return ParameterKind.OUTPUT;
        if (param.IsInArg || entity.LogicalName == "inref`1")
          return ParameterKind.INPUT;

        return ParameterKind.REFERENCE;
      }

      return ParameterKind.VALUE;
    }

    [CanBeNull]
    public static FSharpType TryGetFcsType([NotNull] this IFSharpTreeNode fsTreeNode)
    {
      var checkResults = fsTreeNode.FSharpFile.GetParseAndCheckResults(true, "TryGetFcsType")?.Value?.CheckResults;
      if (checkResults == null) return null;

      var sourceFile = fsTreeNode.GetSourceFile();
      if (sourceFile == null) return null;

      var range = fsTreeNode.GetDocumentRange().ToDocumentRange(sourceFile.GetLocation());
      return checkResults.GetTypeOfExpression(range)?.Value;
    }

    [NotNull]
    public static IType GetExpressionTypeFromFcs([NotNull] this IFSharpTreeNode fsTreeNode)
    {
      var fsharpType = TryGetFcsType(fsTreeNode);
      return fsharpType != null
        ? fsharpType.MapType(fsTreeNode)
        : TypeFactory.CreateUnknownType(fsTreeNode.GetPsiModule());
    }

    [NotNull]
    public static IDeclaredType CreateTypeByClrName([NotNull] this IClrTypeName clrTypeName,
      [NotNull] IPsiModule psiModule) =>
      TypeFactory.CreateTypeByCLRName(clrTypeName, NullableAnnotation.Unknown, psiModule);
  }
}
