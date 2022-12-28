﻿//
// ParameterDeclarationExpression.cs
//
// Author:
//       Mike Krüger <mkrueger@novell.com>
//
// Copyright (c) 2010 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#nullable enable

using dnSpy.Contracts.Text;

namespace ICSharpCode.Decompiler.CSharp.Syntax
{
	public enum ParameterModifier
	{
		None,
		Ref,
		Out,
		Params,
		In,
		Scoped
	}

	public class ParameterDeclaration : AstNode
	{
		public static readonly Role<AttributeSection> AttributeRole = EntityDeclaration.AttributeRole;
		public static readonly TokenRole ThisModifierRole = new TokenRole("this");
		public static readonly TokenRole RefScopedRole = new TokenRole("scoped");
		public static readonly TokenRole RefModifierRole = new TokenRole("ref");
		public static readonly TokenRole OutModifierRole = new TokenRole("out");
		public static readonly TokenRole InModifierRole = new TokenRole("in");
		public static readonly TokenRole ValueScopedRole = new TokenRole("scoped");
		public static readonly TokenRole ParamsModifierRole = new TokenRole("params");

		#region PatternPlaceholder
		public static implicit operator ParameterDeclaration?(PatternMatching.Pattern pattern)
		{
			return pattern != null ? new PatternPlaceholder(pattern) : null;
		}

		sealed class PatternPlaceholder : ParameterDeclaration, PatternMatching.INode
		{
			readonly PatternMatching.Pattern child;

			public PatternPlaceholder(PatternMatching.Pattern child)
			{
				this.child = child;
			}

			public override NodeType NodeType {
				get { return NodeType.Pattern; }
			}

			public override void AcceptVisitor(IAstVisitor visitor)
			{
				visitor.VisitPatternPlaceholder(this, child);
			}

			public override T AcceptVisitor<T>(IAstVisitor<T> visitor)
			{
				return visitor.VisitPatternPlaceholder(this, child);
			}

			public override S AcceptVisitor<T, S>(IAstVisitor<T, S> visitor, T data)
			{
				return visitor.VisitPatternPlaceholder(this, child, data);
			}

			protected internal override bool DoMatch(AstNode? other, PatternMatching.Match match)
			{
				return child.DoMatch(other, match);
			}

			bool PatternMatching.INode.DoMatchCollection(Role role, PatternMatching.INode pos, PatternMatching.Match match, PatternMatching.BacktrackingInfo backtrackingInfo)
			{
				return child.DoMatchCollection(role, pos, match, backtrackingInfo);
			}
		}
		#endregion

		public override NodeType NodeType => NodeType.Unknown;

		public AstNodeCollection<AttributeSection> Attributes {
			get { return GetChildrenByRole(AttributeRole); }
		}

		bool hasThisModifier;
		bool isRefScoped, isValueScoped;

		public CSharpTokenNode ThisKeyword {
			get {
				if (hasThisModifier)
				{
					return GetChildByRole(ThisModifierRole);
				}
				return CSharpTokenNode.Null;
			}
		}

		public bool HasThisModifier {
			get { return hasThisModifier; }
			set {
				ThrowIfFrozen();
				hasThisModifier = value;
			}
		}

		public bool IsRefScoped {
			get { return isRefScoped; }
			set {
				ThrowIfFrozen();
				isRefScoped = value;
			}
		}

		public bool IsValueScoped {
			get { return isValueScoped; }
			set {
				ThrowIfFrozen();
				isValueScoped = value;
			}
		}

		ParameterModifier parameterModifier;

		public ParameterModifier ParameterModifier {
			get { return parameterModifier; }
			set {
				ThrowIfFrozen();
				parameterModifier = value;
			}
		}

		public AstType Type {
			get { return GetChildByRole(Roles.Type); }
			set { SetChildByRole(Roles.Type, value); }
		}

		public string Name {
			get {
				return GetChildByRole(Roles.Identifier).Name;
			}
			set {
				SetChildByRole(Roles.Identifier, Identifier.Create(value));
			}
		}

		public Identifier NameToken {
			get {
				return GetChildByRole(Roles.Identifier);
			}
			set {
				SetChildByRole(Roles.Identifier, value);
			}
		}

		bool hasNullCheck;

		public CSharpTokenNode DoubleExclamationToken {
			get {
				if (hasNullCheck)
				{
					return GetChildByRole(Roles.DoubleExclamation);
				}
				return CSharpTokenNode.Null;
			}
		}

		public bool HasNullCheck {
			get { return hasNullCheck; }
			set {
				ThrowIfFrozen();
				hasNullCheck = value;
			}
		}

		public CSharpTokenNode AssignToken {
			get { return GetChildByRole(Roles.Assign); }
		}

		public Expression DefaultExpression {
			get { return GetChildByRole(Roles.Expression); }
			set { SetChildByRole(Roles.Expression, value); }
		}

		public override void AcceptVisitor(IAstVisitor visitor)
		{
			visitor.VisitParameterDeclaration(this);
		}

		public override T AcceptVisitor<T>(IAstVisitor<T> visitor)
		{
			return visitor.VisitParameterDeclaration(this);
		}

		public override S AcceptVisitor<T, S>(IAstVisitor<T, S> visitor, T data)
		{
			return visitor.VisitParameterDeclaration(this, data);
		}

		protected internal override bool DoMatch(AstNode? other, PatternMatching.Match match)
		{
			var o = other as ParameterDeclaration;
			return o != null && this.Attributes.DoMatch(o.Attributes, match) && this.ParameterModifier == o.ParameterModifier
				&& this.Type.DoMatch(o.Type, match) && MatchString(this.Name, o.Name)
				&& this.HasNullCheck == o.HasNullCheck
				&& this.DefaultExpression.DoMatch(o.DefaultExpression, match);
		}

		public ParameterDeclaration()
		{
		}

		public ParameterDeclaration(AstType type, string name, ParameterModifier modifier = ParameterModifier.None)
		{
			Type = type;
			NameToken = Identifier.Create(name);
			NameToken.AddAnnotation(BoxedTextColor.Parameter);
			ParameterModifier = modifier;
		}

		public ParameterDeclaration(string name, ParameterModifier modifier = ParameterModifier.None)
		{
			Name = name;
			ParameterModifier = modifier;
		}

		public new ParameterDeclaration Clone()
		{
			return (ParameterDeclaration)base.Clone();
		}
	}
}
