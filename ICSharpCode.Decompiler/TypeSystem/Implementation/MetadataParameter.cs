﻿// Copyright (c) 2018 Daniel Grunwald
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.TypeSystem.Implementation
{
	sealed class MetadataParameter : IParameter
	{
		readonly MetadataModule module;
		readonly Parameter handle;

		public IType Type { get; }
		public IParameterizedMember Owner { get; }

		// lazy-loaded:
		string name;
		// these can't be bool? as bool? is not thread-safe from torn reads
		ThreeState constantValueInSignatureState;
		ThreeState decimalConstantState;

		internal MetadataParameter(MetadataModule module, IParameterizedMember owner, IType type, Parameter handle)
		{
			this.module = module;
			this.Owner = owner;
			this.Type = type;
			this.handle = handle;
			if (!IsOptional)
				decimalConstantState = ThreeState.False; // only optional parameters can be constants
		}

		#region Attributes
		public IEnumerable<IAttribute> GetAttributes()
		{
			var b = new AttributeListBuilder(module);

			if (IsOptional && !HasConstantValueInSignature)
				b.Add(KnownAttribute.Optional);

			if (!IsOut && !IsIn) {
				if (handle.HasParamDef) {
					if (handle.ParamDef.IsIn)
						b.Add(KnownAttribute.In);
					if (handle.ParamDef.IsOut)
						b.Add(KnownAttribute.Out);
				}
			}

			if (handle.HasParamDef) {
				b.Add(handle.ParamDef.CustomAttributes);
				b.AddMarshalInfo(handle.ParamDef.MarshalType);
			}

			return b.Build();
		}
		#endregion

		const ParamAttributes inOut = ParamAttributes.In | ParamAttributes.Out;
		public bool IsRef => Type.Kind == TypeKind.ByReference && handle.HasParamDef && (handle.ParamDef.IsIn || !handle.ParamDef.IsOut);
		public bool IsOut => Type.Kind == TypeKind.ByReference && handle.HasParamDef && !handle.ParamDef.IsIn && handle.ParamDef.IsOut;
		public bool IsOptional => handle.HasParamDef && handle.ParamDef.IsOptional;

		public bool IsIn {
			get {
				if (!handle.HasParamDef)
					return false;
				if ((module.TypeSystemOptions & TypeSystemOptions.ReadOnlyStructsAndParameters) == 0 ||
					Type.Kind != TypeKind.ByReference || (handle.ParamDef.Attributes & inOut) != ParamAttributes.In)
					return false;
				return handle.ParamDef.CustomAttributes.HasKnownAttribute(KnownAttribute.IsReadOnly);
			}
		}

		public bool IsParams {
			get {
				if (Type.Kind != TypeKind.Array)
					return false;
				if (!handle.HasParamDef)
					return false;
				return handle.ParamDef.CustomAttributes.HasKnownAttribute(KnownAttribute.ParamArray);
			}
		}

		public string Name {
			get {
				string name = LazyInit.VolatileRead(ref this.name);
				if (name != null)
					return name;
				return LazyInit.GetOrSet(ref this.name, handle.Name);
			}
		}

		bool IVariable.IsConst => false;

		public object ConstantValue {
			get {
				if (!handle.HasParamDef)
					return null;
				if (IsDecimalConstant)
					return DecimalConstantHelper.GetDecimalConstantValue(handle.ParamDef.CustomAttributes);
				if (!handle.ParamDef.HasConstant)
					return null;
				return handle.ParamDef.Constant.Value;
			}
		}

		public bool HasConstantValueInSignature {
			get {
				if (constantValueInSignatureState == ThreeState.Unknown) {
					if (IsDecimalConstant) {
						constantValueInSignatureState = DecimalConstantHelper.AllowsDecimalConstants(module).ToThreeState();
					}
					else if (handle.HasParamDef) {
						constantValueInSignatureState = handle.ParamDef.HasConstant.ToThreeState();
					} else {
						decimalConstantState = ThreeState.False;
					}
				}
				return constantValueInSignatureState == ThreeState.True;
			}
		}

		bool IsDecimalConstant {
			get {
				if (decimalConstantState == ThreeState.Unknown) {
					decimalConstantState = handle.HasParamDef
						? DecimalConstantHelper.IsDecimalConstant(handle.ParamDef.CustomAttributes).ToThreeState()
						: ThreeState.False;
				}
				return decimalConstantState == ThreeState.True;
			}
		}

		SymbolKind ISymbol.SymbolKind => SymbolKind.Parameter;

		public override string ToString()
		{
			return $"NO-TOKEN {DefaultParameter.ToString(this)}";
		}
	}
}
