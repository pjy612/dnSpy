/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Roslyn.Debugger.Formatters.CSharp {
	struct CSharpTypeFormatter {
		readonly IDbgTextWriter output;
		readonly TypeFormatterOptions options;
		readonly CultureInfo cultureInfo;
		const int MAX_RECURSION = 200;
		int recursionCounter;

		const string ARRAY_OPEN_PAREN = "[";
		const string ARRAY_CLOSE_PAREN = "]";
		const string GENERICS_OPEN_PAREN = "<";
		const string GENERICS_CLOSE_PAREN = ">";
		const string TUPLE_OPEN_PAREN = "(";
		const string TUPLE_CLOSE_PAREN = ")";
		const string FNPTR_CALLCONV_OPEN_PAREN = "[";
		const string FNPTR_CALLCONV_CLOSE_PAREN = "]";
		const string FNPTR_OPEN_PAREN = "<";
		const string FNPTR_CLOSE_PAREN = ">";
		const string HEX_PREFIX = "0x";
		const string IDENTIFIER_ESCAPE = "@";
		const string BYREF_KEYWORD = "ref";
		const string IN_KEYWORD = "in";
		const string OUT_KEYWORD = "out";
		const string READONLY_KEYWORD = "readonly";
		const string DELEGATE_KEYWORD = "delegate";
		const int MAX_ARRAY_RANK = 100;

		bool ShowArrayValueSizes => (options & TypeFormatterOptions.ShowArrayValueSizes) != 0;
		bool UseDecimal => (options & TypeFormatterOptions.UseDecimal) != 0;
		bool DigitSeparators => (options & TypeFormatterOptions.DigitSeparators) != 0;
		bool ShowIntrinsicTypeKeywords => (options & TypeFormatterOptions.IntrinsicTypeKeywords) != 0;
		bool ShowTokens => (options & TypeFormatterOptions.Tokens) != 0;
		bool ShowNamespaces => (options & TypeFormatterOptions.Namespaces) != 0;

		public CSharpTypeFormatter(IDbgTextWriter output, TypeFormatterOptions options, CultureInfo? cultureInfo) {
			this.output = output ?? throw new ArgumentNullException(nameof(output));
			this.options = options;
			this.cultureInfo = cultureInfo ?? CultureInfo.InvariantCulture;
			recursionCounter = 0;
		}

		void OutputWrite(string s, DbgTextColor color) => output.Write(color, s);

		void WriteSpace() => OutputWrite(" ", DbgTextColor.Text);

		void WriteCommaSpace() {
			OutputWrite(",", DbgTextColor.Punctuation);
			WriteSpace();
		}

		string ToFormattedDecimalNumber(string number) => ToFormattedNumber(string.Empty, number, ValueFormatterUtils.DigitGroupSizeDecimal);
		string ToFormattedHexNumber(string number) => ToFormattedNumber(HEX_PREFIX, number, ValueFormatterUtils.DigitGroupSizeHex);
		string ToFormattedNumber(string prefix, string number, int digitGroupSize) => ValueFormatterUtils.ToFormattedNumber(DigitSeparators, prefix, number, digitGroupSize);

		string FormatUInt32(uint value) {
			if (UseDecimal)
				return ToFormattedDecimalNumber(value.ToString(cultureInfo));
			else
				return ToFormattedHexNumber(value.ToString("X8"));
		}

		string FormatInt32(int value) {
			if (UseDecimal)
				return ToFormattedDecimalNumber(value.ToString(cultureInfo));
			else
				return ToFormattedHexNumber(value.ToString("X8"));
		}

		void WriteUInt32(uint value) => OutputWrite(FormatUInt32(value), DbgTextColor.Number);
		void WriteInt32(int value) => OutputWrite(FormatInt32(value), DbgTextColor.Number);

		static readonly HashSet<string> isKeyword = new HashSet<string>(StringComparer.Ordinal) {
			"abstract", "as", "base", "bool", "break", "byte", "case", "catch",
			"char", "checked", "class", "const", "continue", "decimal", "default", "delegate",
			"do", "double", "else", "enum", "event", "explicit", "extern", "false",
			"finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
			"in", "int", "interface", "internal", "is", "lock", "long", "namespace",
			"new", "null", "object", "operator", "out", "override", "params", "private",
			"protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
			"sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
			"true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
			"using", "virtual", "void", "volatile", "while",
		};

		internal static string GetFormattedIdentifier(string? id) {
			if (isKeyword.Contains(id!))
				return IDENTIFIER_ESCAPE + IdentifierEscaper.Escape(id);
			return IdentifierEscaper.Escape(id);
		}

		void WriteIdentifier(string? id, DbgTextColor color) => OutputWrite(GetFormattedIdentifier(id), color);

		public void Format(DmdType type, DbgDotNetValue? value = null, IAdditionalTypeInfoProvider? additionalTypeInfoProvider = null, DmdParameterInfo? pd = null, bool forceReadOnly = false) =>
			Format(type, new AdditionalTypeInfoState(additionalTypeInfoProvider), value, pd, forceReadOnly);

		public void Format(DmdType type, AdditionalTypeInfoState state, DbgDotNetValue? value = null, DmdParameterInfo? pd = null, bool forceReadOnly = false) {
			WriteRefIfByRef(type, pd, forceReadOnly);
			if (type.IsByRef) {
				type = type.GetElementType()!;
				state.DynamicTypeIndex++;
			}
			FormatCore(type, value, ref state);
		}

		void FormatCore(DmdType type, DbgDotNetValue? value, ref AdditionalTypeInfoState state) {
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			List<(DmdType type, DbgDotNetValue? value)>? arrayTypesList = null;
			DbgDotNetValue? disposeThisValue = null;
			try {
				if (recursionCounter++ >= MAX_RECURSION)
					return;

				state.DynamicTypeIndex += type.GetCustomModifiers().Count;

				switch (type.TypeSignatureKind) {
				case DmdTypeSignatureKind.SZArray:
				case DmdTypeSignatureKind.MDArray:
					// Array types are shown in reverse order
					arrayTypesList = new List<(DmdType type, DbgDotNetValue? value)>();
					do {
						arrayTypesList.Add((type, arrayTypesList.Count == 0 ? value : null));
						type = type.GetElementType()!;
						state.DynamicTypeIndex++;
					} while (type.IsArray);
					var t = arrayTypesList[arrayTypesList.Count - 1];
					FormatCore(t.type.GetElementType()!, null, ref state);
					foreach (var tuple in arrayTypesList) {
						var aryType = tuple.type;
						var aryValue = tuple.value;
						if (aryType.IsVariableBoundArray) {
							OutputWrite(ARRAY_OPEN_PAREN, DbgTextColor.Punctuation);
							int rank = Math.Min(aryType.GetArrayRank(), MAX_ARRAY_RANK);
							if (rank <= 0)
								OutputWrite("???", DbgTextColor.Error);
							else {
								bool sizesShown = false;
								if (ShowArrayValueSizes) {
									if (aryValue is not null && !aryValue.IsNull && aryValue.GetArrayInfo(out _, out var dimensionInfos) && dimensionInfos.Length == rank) {
										for (int i = 0; i < rank; i++) {
											if (i > 0)
												WriteCommaSpace();
											if (dimensionInfos[i].BaseIndex == 0)
												WriteInt32((int)dimensionInfos[i].Length);
											else {
												WriteInt32(dimensionInfos[i].BaseIndex);
												OutputWrite("..", DbgTextColor.Operator);
												WriteInt32(dimensionInfos[i].BaseIndex + (int)dimensionInfos[i].Length - 1);
											}
										}
										sizesShown = true;
									}
									else {
										var indexes = aryType.GetArrayLowerBounds();
										var sizes = aryType.GetArraySizes();
										if (sizes.Count == rank) {
											for (int i = 0; i < rank; i++) {
												if (i > 0)
													WriteCommaSpace();
												if (i >= indexes.Count || indexes[i] == 0)
													WriteInt32(sizes[i]);
												else {
													WriteInt32(indexes[i]);
													OutputWrite("..", DbgTextColor.Operator);
													WriteInt32(indexes[i] + sizes[i] - 1);
												}
											}
											sizesShown = true;
										}
									}
								}
								if (!sizesShown) {
									if (rank == 1)
										OutputWrite("*", DbgTextColor.Operator);
									OutputWrite(TypeFormatterUtils.GetArrayCommas(rank), DbgTextColor.Punctuation);
								}
							}
							OutputWrite(ARRAY_CLOSE_PAREN, DbgTextColor.Punctuation);
						}
						else {
							Debug.Assert(aryType.IsSZArray);
							OutputWrite(ARRAY_OPEN_PAREN, DbgTextColor.Punctuation);
							if (ShowArrayValueSizes && aryValue is not null && !aryValue.IsNull) {
								if (aryValue.GetArrayCount(out uint elementCount))
									WriteUInt32(elementCount);
							}
							OutputWrite(ARRAY_CLOSE_PAREN, DbgTextColor.Punctuation);
						}
					}
					break;

				case DmdTypeSignatureKind.Pointer:
					state.DynamicTypeIndex++;
					FormatCore(type.GetElementType()!, null, ref state);
					OutputWrite("*", DbgTextColor.Operator);
					break;

				case DmdTypeSignatureKind.ByRef:
					state.DynamicTypeIndex++;
					OutputWrite(BYREF_KEYWORD, DbgTextColor.Keyword);
					WriteSpace();
					FormatCore(type.GetElementType()!, disposeThisValue = value?.LoadIndirect().Value, ref state);
					break;

				case DmdTypeSignatureKind.TypeGenericParameter:
					WriteIdentifier(type.MetadataName, DbgTextColor.TypeGenericParameter);
					break;

				case DmdTypeSignatureKind.MethodGenericParameter:
					WriteIdentifier(type.MetadataName, DbgTextColor.MethodGenericParameter);
					break;

				case DmdTypeSignatureKind.Type:
				case DmdTypeSignatureKind.GenericInstance:
					if (type.IsNullable) {
						state.DynamicTypeIndex++;
						FormatCore(type.GetNullableElementType(), null, ref state);
						OutputWrite("?", DbgTextColor.Operator);
						break;
					}
					if (TypeFormatterUtils.IsSystemValueTuple(type, out int tupleCardinality)) {
						int tupleIndex = state.TupleNameIndex;
						state.TupleNameIndex += tupleCardinality;
						if (tupleCardinality > 1) {
							OutputWrite(TUPLE_OPEN_PAREN, DbgTextColor.Punctuation);
							var tupleType = type;
							for (;;) {
								tupleType = WriteTupleFields(tupleType, ref tupleIndex, ref state);
								if (tupleType is not null) {
									WriteCommaSpace();
									state.DynamicTypeIndex++;
									state.TupleNameIndex += TypeFormatterUtils.GetTupleArity(tupleType);
								}
								else
									break;
							}
							OutputWrite(TUPLE_CLOSE_PAREN, DbgTextColor.Punctuation);
							break;
						}
					}
					var genericArgs = type.GetGenericArguments();
					int genericArgsIndex = 0;
					KeywordType keywordType;
					if (type.DeclaringType is null) {
						keywordType = GetKeywordType(type);
						if (state.TypeInfoProvider is not null) {
							if (keywordType == KeywordType.Object && state.TypeInfoProvider.IsDynamicType(state.DynamicTypeIndex)) {
								OutputWrite("dynamic", DbgTextColor.Keyword);
								break;
							}
							if (type == type.AppDomain.System_IntPtr && state.TypeInfoProvider.IsNativeIntegerType(state.NativeIntTypeIndex++)) {
								OutputWrite("nint", DbgTextColor.Keyword);
								break;
							}
							if (type == type.AppDomain.System_UIntPtr && state.TypeInfoProvider.IsNativeIntegerType(state.NativeIntTypeIndex++)) {
								OutputWrite("nuint", DbgTextColor.Keyword);
								break;
							}
						}
						if (keywordType == KeywordType.NoKeyword)
							WriteNamespace(type);
						WriteTypeName(type, keywordType);
						WriteGenericArguments(type, genericArgs, ref genericArgsIndex, ref state);
					}
					else {
						var typesList = new List<DmdType>();
						typesList.Add(type);
						while (type.DeclaringType is not null) {
							type = type.DeclaringType;
							typesList.Add(type);
						}
						keywordType = GetKeywordType(type);
						if (keywordType == KeywordType.NoKeyword)
							WriteNamespace(type);
						for (int i = typesList.Count - 1; i >= 0; i--) {
							WriteTypeName(typesList[i], i == 0 ? keywordType : KeywordType.NoKeyword);
							WriteGenericArguments(typesList[i], genericArgs, ref genericArgsIndex, ref state);
							if (i != 0)
								OutputWrite(".", DbgTextColor.Operator);
						}
					}
					break;

				case DmdTypeSignatureKind.FunctionPointer:
					var sig = type.GetFunctionPointerMethodSignature();

					OutputWrite(DELEGATE_KEYWORD, DbgTextColor.Keyword);
					OutputWrite("*", DbgTextColor.Operator);

					bool isUnmanaged = (sig.Flags & DmdSignatureCallingConvention.Mask) == DmdSignatureCallingConvention.Unmanaged;
					bool isDefault = (sig.Flags  & DmdSignatureCallingConvention.Mask) == DmdSignatureCallingConvention.Default;
					if (isUnmanaged || !isDefault) {
						WriteSpace();
						OutputWrite("unmanaged", DbgTextColor.Keyword);
					}

					var returnType = sig.ReturnType;
					var customCallConvs = new List<DmdType>();
					foreach (var modifier in returnType.GetCustomModifiers()) {
						if (modifier.Type.Name.StartsWith("CallConv", StringComparison.Ordinal) && modifier.Type.Namespace == "System.Runtime.CompilerServices") {
							state.DynamicTypeIndex++;
							customCallConvs.Add(modifier.Type);
						}
						else
							break;
					}

					bool needsComma = false;
					if (!isDefault || customCallConvs.Count > 0) {
						OutputWrite(FNPTR_CALLCONV_OPEN_PAREN, DbgTextColor.Punctuation);
						if (!isDefault) {
							OutputWrite((sig.Flags & DmdSignatureCallingConvention.Mask) switch {
								DmdSignatureCallingConvention.C => "Cdecl",
								DmdSignatureCallingConvention.StdCall => "Stdcall",
								DmdSignatureCallingConvention.ThisCall => "Thiscall",
								DmdSignatureCallingConvention.FastCall => "Fastcall",
								DmdSignatureCallingConvention.VarArg => "Varargs",
								_ => sig.Flags.ToString()
							}, DbgTextColor.Keyword);
							needsComma = true;
						}

						for (var i = 0; i < customCallConvs.Count; i++) {
							if (needsComma)
								WriteCommaSpace();
							needsComma = true;
							var customCallConv = customCallConvs[i];
							if (customCallConv.Name.StartsWith("CallConv", StringComparison.Ordinal) &&
								customCallConv.Name.Length > 8)
								OutputWrite(customCallConv.Name.Substring(8), DbgTextColor.Keyword);
							else
								Format(customCallConv);
						}

						OutputWrite(FNPTR_CALLCONV_CLOSE_PAREN, DbgTextColor.Punctuation);
					}

					OutputWrite(FNPTR_OPEN_PAREN, DbgTextColor.Punctuation);

					state.DynamicTypeIndex++;
					var parametersState = state;
					TypeFormatterUtils.UpdateTypeState(returnType, ref parametersState);

					needsComma = false;
					var parameters = sig.GetParameterTypes();
					for (int i = 0; i < parameters.Count; i++) {
						if (needsComma)
							WriteCommaSpace();
						needsComma = true;

						parametersState.DynamicTypeIndex++;
						var paramType = parameters[i];
						bool modifierWritten = false;

						var modifiers = paramType.GetRequiredCustomModifiers();

						if (modifiers.Length > 0) {
							string? modifier = modifiers[0].FullName;
							if (modifier == "System.Runtime.InteropServices.InAttribute") {
								modifierWritten = true;
								OutputWrite(IN_KEYWORD, DbgTextColor.Keyword);
								WriteSpace();
								parametersState.DynamicTypeIndex++;
							}
							else if (modifier == "System.Runtime.InteropServices.OutAttribute") {
								modifierWritten = true;
								OutputWrite(OUT_KEYWORD, DbgTextColor.Keyword);
								WriteSpace();
								parametersState.DynamicTypeIndex++;
							}
						}
						if (paramType.IsByRef) {
							if (!modifierWritten) {
								OutputWrite(BYREF_KEYWORD, DbgTextColor.Keyword);
								WriteSpace();
							}
							parametersState.DynamicTypeIndex++;
							paramType = paramType.GetElementType()!;
						}

						FormatCore(paramType, null, ref parametersState);
					}

					if (needsComma)
						WriteCommaSpace();
					FormatCore(returnType, null, ref state);

					OutputWrite(FNPTR_CLOSE_PAREN, DbgTextColor.Punctuation);
					break;

				default:
					throw new InvalidOperationException();
				}
			}
			finally {
				recursionCounter--;
				if (arrayTypesList is not null) {
					foreach (var info in arrayTypesList) {
						if (info.value != value)
							info.value?.Dispose();
					}
				}
				disposeThisValue?.Dispose();
			}
		}

		void WriteGenericArguments(DmdType type, IList<DmdType> genericArgs, ref int genericArgsIndex, ref AdditionalTypeInfoState state) {
			var gas = type.GetGenericArguments();
			if (genericArgsIndex < genericArgs.Count && genericArgsIndex < gas.Count) {
				OutputWrite(GENERICS_OPEN_PAREN, DbgTextColor.Punctuation);
				int startIndex = genericArgsIndex;
				for (int j = startIndex; j < genericArgs.Count && j < gas.Count; j++, genericArgsIndex++) {
					if (j > startIndex)
						WriteCommaSpace();
					state.DynamicTypeIndex++;
					FormatCore(genericArgs[j], null, ref state);
				}
				OutputWrite(GENERICS_CLOSE_PAREN, DbgTextColor.Punctuation);
			}
		}

		DmdType? WriteTupleFields(DmdType type, ref int index, ref AdditionalTypeInfoState state) {
			var args = type.GetGenericArguments();
			Debug.Assert(0 < args.Count && args.Count <= TypeFormatterUtils.MAX_TUPLE_ARITY);
			if (args.Count > TypeFormatterUtils.MAX_TUPLE_ARITY) {
				OutputWrite("???", DbgTextColor.Error);
				return null;
			}
			for (int i = 0; i < args.Count && i < TypeFormatterUtils.MAX_TUPLE_ARITY - 1; i++) {
				if (i > 0)
					WriteCommaSpace();
				state.DynamicTypeIndex++;
				FormatCore(args[i], null, ref state);
				string? fieldName = state.TypeInfoProvider?.GetTupleElementName(index++);
				if (fieldName is not null) {
					WriteSpace();
					OutputWrite(fieldName, DbgTextColor.InstanceField);
				}
			}
			if (args.Count == TypeFormatterUtils.MAX_TUPLE_ARITY)
				return args[TypeFormatterUtils.MAX_TUPLE_ARITY - 1];
			return null;
		}

		void WriteRefIfByRef(DmdType type, DmdParameterInfo? pd, bool forceReadOnly) {
			if (!type.IsByRef)
				return;
			if (pd is not null && (!pd.IsIn && pd.IsOut)) {
				OutputWrite(OUT_KEYWORD, DbgTextColor.Keyword);
				WriteSpace();
			}
			else if (pd is not null && (pd.IsIn && !pd.IsOut && TypeFormatterUtils.IsReadOnlyParameter(pd))) {
				OutputWrite(IN_KEYWORD, DbgTextColor.Keyword);
				WriteSpace();
			}
			else {
				OutputWrite(BYREF_KEYWORD, DbgTextColor.Keyword);
				WriteSpace();
				if (forceReadOnly) {
					OutputWrite(READONLY_KEYWORD, DbgTextColor.Keyword);
					WriteSpace();
				}
			}
		}

		void WriteNamespace(DmdType type) {
			if (!ShowNamespaces)
				return;
			var ns = type.MetadataNamespace;
			if (string2.IsNullOrEmpty(ns))
				return;
			foreach (var nsPart in ns.Split(namespaceSeparators)) {
				WriteIdentifier(nsPart, DbgTextColor.Namespace);
				OutputWrite(".", DbgTextColor.Operator);
			}
		}
		static readonly char[] namespaceSeparators = new[] { '.' };

		void WriteTypeName(DmdType type, KeywordType keywordType) {
			switch (keywordType) {
			case KeywordType.Void:		OutputWrite("void", DbgTextColor.Keyword); return;
			case KeywordType.Boolean:	OutputWrite("bool", DbgTextColor.Keyword); return;
			case KeywordType.Char:		OutputWrite("char", DbgTextColor.Keyword); return;
			case KeywordType.SByte:		OutputWrite("sbyte", DbgTextColor.Keyword); return;
			case KeywordType.Byte:		OutputWrite("byte", DbgTextColor.Keyword); return;
			case KeywordType.Int16:		OutputWrite("short", DbgTextColor.Keyword); return;
			case KeywordType.UInt16:	OutputWrite("ushort", DbgTextColor.Keyword); return;
			case KeywordType.Int32:		OutputWrite("int", DbgTextColor.Keyword); return;
			case KeywordType.UInt32:	OutputWrite("uint", DbgTextColor.Keyword); return;
			case KeywordType.Int64:		OutputWrite("long", DbgTextColor.Keyword); return;
			case KeywordType.UInt64:	OutputWrite("ulong", DbgTextColor.Keyword); return;
			case KeywordType.Single:	OutputWrite("float", DbgTextColor.Keyword); return;
			case KeywordType.Double:	OutputWrite("double", DbgTextColor.Keyword); return;
			case KeywordType.Object:	OutputWrite("object", DbgTextColor.Keyword); return;
			case KeywordType.Decimal:	OutputWrite("decimal", DbgTextColor.Keyword); return;
			case KeywordType.String:	OutputWrite("string", DbgTextColor.Keyword); return;

			case KeywordType.NoKeyword:
				break;

			default:
				throw new InvalidOperationException();
			}

			WriteIdentifier(TypeFormatterUtils.RemoveGenericTick(type.MetadataName ?? string.Empty), TypeFormatterUtils.GetColor(type, canBeModule: false));
			new CSharpPrimitiveValueFormatter(output, options.ToValueFormatterOptions(), cultureInfo).WriteTokenComment(type.MetadataToken);
		}

		enum KeywordType {
			NoKeyword,
			Void,
			Boolean,
			Char,
			SByte,
			Byte,
			Int16,
			UInt16,
			Int32,
			UInt32,
			Int64,
			UInt64,
			Single,
			Double,
			Object,
			Decimal,
			String,
		}

		KeywordType GetKeywordType(DmdType type) {
			const KeywordType defaultValue = KeywordType.NoKeyword;
			if (!ShowIntrinsicTypeKeywords)
				return defaultValue;
			if (type.MetadataNamespace == "System" && !type.IsNested) {
				switch (type.MetadataName) {
				case "Void":	return type == type.AppDomain.System_Void		? KeywordType.Void		: defaultValue;
				case "Boolean":	return type == type.AppDomain.System_Boolean	? KeywordType.Boolean	: defaultValue;
				case "Char":	return type == type.AppDomain.System_Char		? KeywordType.Char		: defaultValue;
				case "SByte":	return type == type.AppDomain.System_SByte		? KeywordType.SByte		: defaultValue;
				case "Byte":	return type == type.AppDomain.System_Byte		? KeywordType.Byte		: defaultValue;
				case "Int16":	return type == type.AppDomain.System_Int16		? KeywordType.Int16		: defaultValue;
				case "UInt16":	return type == type.AppDomain.System_UInt16		? KeywordType.UInt16	: defaultValue;
				case "Int32":	return type == type.AppDomain.System_Int32		? KeywordType.Int32		: defaultValue;
				case "UInt32":	return type == type.AppDomain.System_UInt32		? KeywordType.UInt32	: defaultValue;
				case "Int64":	return type == type.AppDomain.System_Int64		? KeywordType.Int64		: defaultValue;
				case "UInt64":	return type == type.AppDomain.System_UInt64		? KeywordType.UInt64	: defaultValue;
				case "Single":	return type == type.AppDomain.System_Single		? KeywordType.Single	: defaultValue;
				case "Double":	return type == type.AppDomain.System_Double		? KeywordType.Double	: defaultValue;
				case "Object":	return type == type.AppDomain.System_Object		? KeywordType.Object	: defaultValue;
				case "Decimal":	return type == type.AppDomain.System_Decimal	? KeywordType.Decimal	: defaultValue;
				case "String":	return type == type.AppDomain.System_String		? KeywordType.String	: defaultValue;
				}
			}
			return defaultValue;
		}
	}
}
