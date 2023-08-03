using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using dnlib.DotNet;
using dnlib.DotNet.Emit;

using dnSpy.Contracts.Decompiler;

using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.CSharp
{
	internal enum MethodKind : byte {
		Method,
		Property,
		Event,
	}

	public partial class CSharpAstBuilder
	{
		private readonly DecompilerContext context;
		private SyntaxTree syntaxTree;
		private readonly Dictionary<string, NamespaceDeclaration> astNamespaces = new Dictionary<string, NamespaceDeclaration>();
		private bool transformationsHaveRun;
		private readonly StringBuilder stringBuilder;// PERF: prevent extra created strings
		private readonly char[] commentBuffer;// PERF: prevent extra created strings
		private readonly List<Task<AsyncMethodBodyResult>> methodBodyTasks = new List<Task<AsyncMethodBodyResult>>();
		private readonly List<AsyncMethodBodyDecompilationState> asyncMethodBodyDecompilationStates = new List<AsyncMethodBodyDecompilationState>();
		private readonly List<Comment> comments = new List<Comment>();
		private IDecompilerTypeSystem typeSystem;
		private TypeSystemAstBuilder typeSystemAstBuilder;
		private ITypeResolveContext currentTypeResolveContext;
		private DecompileRun currentDecompileRun;

		public DecompilerContext Context => this.context;

		public SyntaxTree SyntaxTree => this.syntaxTree;

		private const int COMMENT_BUFFER_LENGTH = 2 + 8;

		public Func<CSharpAstBuilder, MethodDef, DecompiledBodyKind> GetDecompiledBodyKind { get; set; }

		public CSharpAstBuilder(DecompilerContext context)
		{
			this.context = context ?? throw new ArgumentNullException(nameof(context));
			this.stringBuilder = new StringBuilder();
			this.commentBuffer = new char[COMMENT_BUFFER_LENGTH];
			this.syntaxTree = new SyntaxTree();
			this.transformationsHaveRun = false;
			this.GetDecompiledBodyKind = null;
			this.typeSystem = null;
			this.typeSystemAstBuilder = null;
			this.currentTypeResolveContext = null;
			this.currentDecompileRun = null;
		}

		public void Reset()
		{
			this.typeSystem = null;
			this.typeSystemAstBuilder = null;
			this.currentTypeResolveContext = null;
			this.currentDecompileRun = null;
			this.GetDecompiledBodyKind = null;
			this.syntaxTree = new SyntaxTree();
			this.transformationsHaveRun = false;
			this.astNamespaces.Clear();
			this.stringBuilder.Clear();
			this.context.Reset();
			this.methodBodyTasks.Clear();
		}

		public void InitializeTypeSystem()
		{
			typeSystem = new DecompilerTypeSystem(new PEFile(context.CurrentModule), context.Settings);
			typeSystemAstBuilder = new TypeSystemAstBuilder {
				ShowAttributes = true,
				AlwaysUseShortTypeNames = true,
				AddResolveResultAnnotations = true,
				UseNullableSpecifierForValueTypes = context.Settings.LiftNullables,
				SupportInitAccessors = context.Settings.InitAccessors,
				SupportRecordClasses = context.Settings.RecordClasses,
				SupportRecordStructs = context.Settings.RecordStructs,
				AlwaysUseGlobal = context.Settings.AlwaysUseGlobal,
				TypeAddInternalModifier = context.Settings.TypeAddInternalModifier,
				MemberAddPrivateModifier = context.Settings.MemberAddPrivateModifier,
				SupportUnsignedRightShift = context.Settings.UnsignedRightShift,
				SupportOperatorChecked = context.Settings.CheckedOperators,
			};
			currentTypeResolveContext =
				new SimpleTypeResolveContext(typeSystem.MainModule).WithCurrentTypeDefinition(
					typeSystem.MainModule.GetDefinition(context.CurrentType));
			currentDecompileRun = new DecompileRun(context.Settings) { CancellationToken = context.CancellationToken, Context = context};
		}

		public void AddAssembly(ModuleDef moduleDefinition, bool decompileAsm, bool decompileMod)
		{
			if (decompileAsm && moduleDefinition.Assembly != null)
			{
				RequiredNamespaceCollector.CollectAttributeNamespacesOnlyAssembly(typeSystem.MainModule, currentDecompileRun.Namespaces);
				foreach (var a in typeSystem.MainModule.GetAssemblyAttributes()) {
					var attrSection = new AttributeSection(typeSystemAstBuilder.ConvertAttribute(a)) {
						AttributeTarget =  "assembly"
					};
					syntaxTree.AddChild(attrSection, SyntaxTree.MemberRole);
				}
			}

			if (decompileMod)
			{
				RequiredNamespaceCollector.CollectAttributeNamespacesOnlyModule(typeSystem.MainModule, currentDecompileRun.Namespaces);
				foreach (var a in typeSystem.MainModule.GetModuleAttributes()) {
					var attrSection = new AttributeSection(typeSystemAstBuilder.ConvertAttribute(a)) {
						AttributeTarget = "module"
					};
					syntaxTree.AddChild(attrSection, SyntaxTree.MemberRole);
				}
			}
		}

		private NamespaceDeclaration GetCodeNamespace(string name, IAssembly asm)
		{
			if (string.IsNullOrEmpty(name))
				return null;
			if (astNamespaces.TryGetValue(name, out var astNamespace))
				return astNamespace;

			// Create the namespace
			astNamespace = new NamespaceDeclaration(name, asm);
			syntaxTree.Members.Add(astNamespace);
			astNamespaces[name] = astNamespace;
			return astNamespace;
		}

		public void AddType(TypeDef typeDef)
		{
			var astType = CreateType(typeDef);
			NamespaceDeclaration astNS = GetCodeNamespace(typeDef.Namespace, typeDef.DefinitionAssembly);
			if (astNS is null)
				syntaxTree.Members.Add(astType);
			else
				astNS.Members.Add(astType);
		}

		public void AddMethod(MethodDef method)
		{
			syntaxTree.Members.Add(CreateMethod(method));
		}

		public void AddProperty(PropertyDef property)
		{
			syntaxTree.Members.Add(CreateProperty(property));
		}

		public void AddField(FieldDef field)
		{
			syntaxTree.Members.Add(CreateField(field));
		}

		public void AddEvent(EventDef ev)
		{
			syntaxTree.Members.Add(CreateEvent(ev));
		}

		public EntityDeclaration CreateType(TypeDef typeDef)
		{
			TypeDef oldCurrentType = context.CurrentType;
			var oldResolveContext = currentTypeResolveContext;
			context.CurrentType = typeDef;

			var tsTypeDef = typeSystem.MainModule.GetDefinition(typeDef);

			currentTypeResolveContext = currentTypeResolveContext.WithCurrentTypeDefinition(tsTypeDef);

			var entityDecl = typeSystemAstBuilder.ConvertEntity(tsTypeDef);
			if (entityDecl is not TypeDeclaration typeDecl)
			{
				RequiredNamespaceCollector.CollectNamespaces(tsTypeDef, typeSystem.MainModule, currentDecompileRun.Namespaces);
				if (entityDecl is DelegateDeclaration dd)
				{
					// Fix empty parameter names in delegate declarations
					FixParameterNames(dd);
					AddComment(dd, (MethodDef)tsTypeDef.GetDelegateInvokeMethod().MetadataToken, "Invoke");
					AddComment(dd, typeDef);
				}

				// e.g. DelegateDeclaration
				context.CurrentType = oldCurrentType;
				currentTypeResolveContext = oldResolveContext;
				return entityDecl;
			}

			RequiredNamespaceCollector.CollectNamespacesOnlyType(tsTypeDef, currentDecompileRun.Namespaces);

			bool isRecord = tsTypeDef.Kind switch {
				TypeKind.Class => context.Settings.RecordClasses && tsTypeDef.IsRecord,
				TypeKind.Struct => context.Settings.RecordStructs && tsTypeDef.IsRecord,
				_ => false,
			};
			RecordDecompiler recordDecompiler = isRecord ? new RecordDecompiler(typeSystem, tsTypeDef, context.Settings, context.CancellationToken) : null;
			if (recordDecompiler != null)
				currentDecompileRun.RecordDecompilers.Add(tsTypeDef, recordDecompiler);

			if (recordDecompiler?.PrimaryConstructor != null)
			{
				foreach (var p in recordDecompiler.PrimaryConstructor.Parameters)
				{
					ParameterDeclaration pd = typeSystemAstBuilder.ConvertParameter(p);
					(IProperty prop, ICSharpCode.Decompiler.TypeSystem.IField field) = recordDecompiler.GetPropertyInfoByPrimaryConstructorParameter(p);
					Syntax.Attribute[] attributes = prop.GetAttributes().Select(attr => typeSystemAstBuilder.ConvertAttribute(attr)).ToArray();
					if (attributes.Length > 0)
					{
						var section = new AttributeSection {
							AttributeTarget = "property"
						};
						section.Attributes.AddRange(attributes);
						pd.Attributes.Add(section);
					}
					attributes = field.GetAttributes()
									  .Where(a => !PatternStatementTransform.attributeTypesToRemoveFromAutoProperties.Contains(a.AttributeType.FullName))
									  .Select(attr => typeSystemAstBuilder.ConvertAttribute(attr)).ToArray();
					if (attributes.Length > 0)
					{
						var section = new AttributeSection {
							AttributeTarget = "field"
						};
						section.Attributes.AddRange(attributes);
						pd.Attributes.Add(section);
					}
					typeDecl.PrimaryConstructorParameters.Add(pd);
				}
			}

			currentDecompileRun.EnumValueDisplayMode = tsTypeDef.Kind == TypeKind.Enum
				? DetectBestEnumValueDisplayMode(tsTypeDef)
				: null;

			// With C# 9 records, the relative order of fields and properties matters:
			if (recordDecompiler?.FieldsAndProperties is null)
			{
				AddTypeMembers(typeDecl, typeDef);
			}
			else
			{
				foreach (var type in GetNestedTypes(typeDef))
				{
					if (!MemberIsHidden(type, context.Settings))
					{
						var nestedType = CreateType(type);
						SetNewModifier(nestedType);
						typeDecl.Members.Add(nestedType);
					}
				}

				foreach (var fieldOrProperty in recordDecompiler.FieldsAndProperties)
				{
					if (MemberIsHidden(fieldOrProperty.MetadataToken, context.Settings))
					{
						continue;
					}
					if (fieldOrProperty is ICSharpCode.Decompiler.TypeSystem.IField field)
					{
						if (tsTypeDef.Kind == TypeKind.Enum && !field.IsConst)
							continue;
						typeDecl.Members.Add(CreateField((FieldDef)field.MetadataToken));
					}
					else if (fieldOrProperty is IProperty property)
					{
						if (recordDecompiler?.PropertyIsGenerated(property) == true)
						{
							continue;
						}
						typeDecl.Members.Add(CreateProperty(property.MetadataToken));
					}
				}
				foreach (var @event in tsTypeDef.Events) {
					if (!MemberIsHidden(@event.MetadataToken,context.Settings))
					{
						typeDecl.Members.Add(CreateEvent(@event.MetadataToken));
					}
				}
				foreach (var method in tsTypeDef.Methods)
				{
					if (recordDecompiler?.MethodIsGenerated(method) == true)
					{
						continue;
					}
					// Check if this is a fake method.
					if (method.MetadataToken is null)
						continue;
					if (!MemberIsHidden(method.MetadataToken, context.Settings))
					{
						var memberDecl = CreateMethod((MethodDef)method.MetadataToken);
						typeDecl.Members.Add(memberDecl);
						typeDecl.Members.AddRange(AddInterfaceImplHelpers(memberDecl, method, typeSystemAstBuilder));
					}
				}
			}

			if (typeDecl.Members.OfType<IndexerDeclaration>().Any(idx => idx.PrivateImplementationType.IsNull))
			{
				// Remove the [DefaultMember] attribute if the class contains indexers
				RemoveAttribute(typeDecl, KnownAttribute.DefaultMember);
			}
			if (context.Settings.IntroduceRefModifiersOnStructs)
			{
				RemoveObsoleteAttribute(typeDecl, "Types with embedded references are not supported in this version of your compiler.");
				RemoveCompilerFeatureRequiredAttribute(typeDecl, "RefStructs");
			}
			if (context.Settings.RequiredMembers)
			{
				RemoveAttribute(typeDecl, KnownAttribute.RequiredAttribute);
			}
			if (typeDecl.ClassType == ClassType.Enum)
			{
				switch (currentDecompileRun.EnumValueDisplayMode)
				{
					case EnumValueDisplayMode.FirstOnly:
						foreach (var enumMember in typeDecl.Members.OfType<EnumMemberDeclaration>().Skip(1))
						{
							enumMember.Initializer = null;
						}
						break;
					case EnumValueDisplayMode.None:
						foreach (var enumMember in typeDecl.Members.OfType<EnumMemberDeclaration>())
						{
							enumMember.Initializer = null;
							if (enumMember.GetSymbol() is ICSharpCode.Decompiler.TypeSystem.IField f && f.GetConstantValue() == null)
							{
								typeDecl.InsertChildBefore(enumMember, new Comment(" error: enumerator has no value"), Roles.Comment);
							}
						}
						break;
					case EnumValueDisplayMode.All:
					case EnumValueDisplayMode.AllHex:
						// nothing needs to be changed.
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
				currentDecompileRun.EnumValueDisplayMode = null;
			}

			AddComment(typeDecl, typeDef);

			context.CurrentType = oldCurrentType;
			currentTypeResolveContext = oldResolveContext;
			return typeDecl;
		}

		private EntityDeclaration CreateMethod(MethodDef methodDef)
		{
			var tsMethod = typeSystem.MainModule.GetDefinition(methodDef);

			RequiredNamespaceCollector.CollectNamespaces(tsMethod, typeSystem.MainModule, currentDecompileRun.Namespaces);

			var methodDecl = tsMethod.IsAccessor ? typeSystemAstBuilder.ConvertMethod(tsMethod) : typeSystemAstBuilder.ConvertEntity(tsMethod);

			int lastDot = tsMethod.Name.LastIndexOf('.');
			if (tsMethod.IsExplicitInterfaceImplementation && lastDot >= 0)
			{
				methodDecl.NameToken.Name = tsMethod.Name.Substring(lastDot + 1);
			}
			FixParameterNames(methodDecl);

			if (!context.Settings.LocalFunctions && LocalFunctionDecompiler.LocalFunctionNeedsAccessibilityChange(null, methodDef))
			{
				// if local functions are not active and we're dealing with a local function,
				// reduce the visibility of the method to private,
				// otherwise this leads to compile errors because the display classes have lesser accessibility.
				// Note: removing and then adding the static modifier again is necessary to set the private modifier before all other modifiers.
				methodDecl.Modifiers &= ~(Modifiers.Internal | Modifiers.Static);
				methodDecl.Modifiers |= Modifiers.Private | (tsMethod.IsStatic ? Modifiers.Static : 0);
			}

			if (methodDef.HasBody)
			{
				var parameters = methodDecl.GetChildrenByRole(Roles.Parameter);
				AddMethodBody(methodDecl, methodDef, tsMethod,
					currentTypeResolveContext.WithCurrentMember(tsMethod), parameters, false, MethodKind.Method);
			}
			else if (!tsMethod.IsAbstract && tsMethod.DeclaringType.Kind != TypeKind.Interface)
			{
				methodDecl.Modifiers |= Modifiers.Extern;
			}
			if (tsMethod.SymbolKind == SymbolKind.Method && !tsMethod.IsExplicitInterfaceImplementation && methodDef.IsVirtual == methodDef.IsNewSlot)
			{
				SetNewModifier(methodDecl);
			}
			else if (!tsMethod.IsStatic && !tsMethod.IsExplicitInterfaceImplementation
										&& !tsMethod.IsVirtual && tsMethod.IsOverride
										&& InheritanceHelper.GetBaseMember(tsMethod) == null && IsTypeHierarchyKnown(tsMethod.DeclaringType))
			{
				methodDecl.Modifiers &= ~Modifiers.Override;
				if (!tsMethod.DeclaringTypeDefinition.IsSealed)
					methodDecl.Modifiers |= Modifiers.Virtual;
			}
			if (IsCovariantReturnOverride(tsMethod))
			{
				RemoveAttribute(methodDecl, KnownAttribute.PreserveBaseOverrides);
				methodDecl.Modifiers &= ~(Modifiers.New | Modifiers.Virtual);
				methodDecl.Modifiers |= Modifiers.Override;
			}
			if (methodDef.IsConstructor && context.Settings.RequiredMembers && RemoveCompilerFeatureRequiredAttribute(methodDecl, "RequiredMembers"))
			{
				RemoveObsoleteAttribute(methodDecl, "Constructors of types with required members are not supported in this version of your compiler.");
			}

			if (methodDef.IsConstructor && methodDef.IsStatic && methodDef.DeclaringType.IsBeforeFieldInit)
				methodDecl.InsertChildAfter(null, new Comment(" Note: this type is marked as 'beforefieldinit'."), Roles.Comment);

			AddComment(methodDecl, methodDef);

			bool IsTypeHierarchyKnown(ICSharpCode.Decompiler.TypeSystem.IType type)
			{
				var definition = type.GetDefinition();
				if (definition is null)
					return false;

				if (currentDecompileRun.TypeHierarchyIsKnown.TryGetValue(definition, out var value))
					return value;
				value = tsMethod.DeclaringType.GetNonInterfaceBaseTypes().All(t => t.Kind != TypeKind.Unknown);
				currentDecompileRun.TypeHierarchyIsKnown.Add(definition, value);
				return value;
			}

			return methodDecl;
		}

		private IEnumerable<InterfaceImpl> GetInterfaceImpls(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.Interfaces;// These are already sorted by MD token
			return type.GetInterfaceImpls(context.Settings.SortMembers);
		}

		private IEnumerable<TypeDef> GetNestedTypes(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.NestedTypes;// These are already sorted by MD token
			return type.GetNestedTypes(context.Settings.SortMembers);
		}

		private IEnumerable<FieldDef> GetFields(TypeDef type)
		{
			if (context.Settings.UseSourceCodeOrder)
				return type.Fields;// These are already sorted by MD token
			return type.GetFields(context.Settings.SortMembers);
		}

		private void AddTypeMembers(TypeDeclaration astType, TypeDef typeDef)
		{
			bool hasShownMethods = false;
			foreach (var d in this.context.Settings.DecompilationObjects) {
				switch (d) {
				case DecompilationObject.NestedTypes:
					foreach (TypeDef nestedTypeDef in GetNestedTypes(typeDef)) {
						if (MemberIsHidden(nestedTypeDef, context.Settings))
							continue;
						var nestedType = CreateType(nestedTypeDef);
						SetNewModifier(nestedType);
						astType.AddChild(nestedType, Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Fields:
					foreach (FieldDef fieldDef in GetFields(typeDef)) {
						if (typeDef.IsEnum && !fieldDef.IsStatic)
							continue;
						if (MemberIsHidden(fieldDef, context.Settings)) continue;
						astType.AddChild(CreateField(fieldDef), Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Events:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					foreach (EventDef eventDef in typeDef.GetEvents(context.Settings.SortMembers)) {
						if (eventDef.AddMethod == null && eventDef.RemoveMethod == null)
							continue;
						astType.AddChild(CreateEvent(eventDef), Roles.TypeMemberRole);
					}
					break;

				case DecompilationObject.Properties:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					foreach (PropertyDef propDef in typeDef.GetProperties(context.Settings.SortMembers)) {
						if (propDef.GetMethod == null && propDef.SetMethod == null)
							continue;
						astType.Members.Add(CreateProperty(propDef));
					}
					break;

				case DecompilationObject.Methods:
					if (hasShownMethods)
						break;
					if (context.Settings.UseSourceCodeOrder || !typeDef.CanSortMethods()) {
						ShowAllMethods(astType, typeDef);
						hasShownMethods = true;
						break;
					}
					foreach (MethodDef methodDef in typeDef.GetMethods(context.Settings.SortMembers)) {
						if (MemberIsHidden(methodDef, context.Settings)) continue;

						var memberDecl = CreateMethod(methodDef);
						astType.Members.Add(memberDecl);
						astType.Members.AddRange(AddInterfaceImplHelpers(memberDecl, typeSystem.MainModule.GetDefinition(methodDef), typeSystemAstBuilder));
					}
					break;

				default: throw new InvalidOperationException();
				}
			}
		}

		private void ShowAllMethods(TypeDeclaration astType, TypeDef type)
		{
			foreach (var def in type.GetNonSortedMethodsPropertiesEvents()) {
				if (def is MethodDef md) {
					if (MemberIsHidden(md, context.Settings))
						continue;
					astType.Members.Add(CreateMethod(md));
					continue;
				}

				if (def is PropertyDef pd) {
					if (pd.GetMethod == null && pd.SetMethod == null)
						continue;
					astType.Members.Add(CreateProperty(pd));
					continue;
				}

				if (def is EventDef ed) {
					if (ed.AddMethod == null && ed.RemoveMethod == null)
						continue;
					astType.AddChild(CreateEvent(ed), Roles.TypeMemberRole);
					continue;
				}

				Debug.Fail("Shouldn't be here");
			}
		}

		private EntityDeclaration CreateField(FieldDef fieldDef)
		{
			var tsField = typeSystem.MainModule.GetDefinition(fieldDef);

			RequiredNamespaceCollector.CollectNamespaces(tsField, typeSystem.MainModule, currentDecompileRun.Namespaces);

			if (currentTypeResolveContext.CurrentTypeDefinition!.Kind == TypeKind.Enum && tsField.IsConst) {
				var enumDec = new EnumMemberDeclaration();
				enumDec.WithAnnotation(fieldDef);
				enumDec.NameToken = Identifier.Create(tsField.Name).WithAnnotation(fieldDef);
				object constantValue = tsField.GetConstantValue();
				if (constantValue != null) {
					long initValue = (long)CSharpPrimitiveCast.Cast(TypeCode.Int64, constantValue, false);
					enumDec.Initializer = typeSystemAstBuilder.ConvertConstantValue(currentTypeResolveContext.CurrentTypeDefinition.EnumUnderlyingType, constantValue);
					if (enumDec.Initializer is PrimitiveExpression primitive
						&& initValue >= 10 && currentDecompileRun.EnumValueDisplayMode == EnumValueDisplayMode.AllHex)
					{
						primitive.Format = LiteralFormat.HexadecimalNumber;
					}
				}

				enumDec.Attributes.AddRange(tsField.GetAttributes().Select(a => new AttributeSection(typeSystemAstBuilder.ConvertAttribute(a))));
				enumDec.AddAnnotation(new MemberResolveResult(null, tsField));
				AddComment(enumDec, fieldDef);
				return enumDec;
			}

			bool oldUseSpecialConstants = typeSystemAstBuilder.UseSpecialConstants;
			bool isMathPIOrE = (tsField.Name == "PI" || tsField.Name == "E") && (tsField.DeclaringType.FullName == "System.Math" || tsField.DeclaringType.FullName == "System.MathF");
			typeSystemAstBuilder.UseSpecialConstants = !(tsField.DeclaringType.Equals(tsField.ReturnType) || isMathPIOrE);

			var fieldDecl = typeSystemAstBuilder.ConvertEntity(tsField);
			SetNewModifier(fieldDecl);

			if (context.Settings.RequiredMembers && RemoveAttribute(fieldDecl, KnownAttribute.RequiredAttribute))
			{
				fieldDecl.Modifiers |= Modifiers.Required;
			}
			if (context.Settings.FixedBuffers && IsFixedField(tsField, out var elementType, out var elementCount)) {
				var fixedFieldDecl = new FixedFieldDeclaration();
				fieldDecl.Attributes.MoveTo(fixedFieldDecl.Attributes);
				fixedFieldDecl.Modifiers = fieldDecl.Modifiers;
				fixedFieldDecl.ReturnType = typeSystemAstBuilder.ConvertType(elementType);
				fixedFieldDecl.Variables.Add(new FixedVariableInitializer {
					NameToken = Identifier.Create(tsField.Name).WithAnnotation(fieldDef),
					CountExpression = new PrimitiveExpression(elementCount)
				}.WithAnnotation(fieldDef));
				fixedFieldDecl.Variables.Single().CopyAnnotationsFrom(((FieldDeclaration)fieldDecl).Variables.Single());
				fixedFieldDecl.CopyAnnotationsFrom(fieldDecl);

				RemoveAttribute(fixedFieldDecl, KnownAttribute.FixedBuffer);

				fieldDecl = fixedFieldDecl;
			}

			AddComment(fieldDecl, fieldDef);

			typeSystemAstBuilder.UseSpecialConstants = oldUseSpecialConstants;
			return fieldDecl;
		}

		private EntityDeclaration CreateProperty(PropertyDef propertyDef)
		{
			var tsProperty = typeSystem.MainModule.GetDefinition(propertyDef);

			RequiredNamespaceCollector.CollectNamespaces(tsProperty, typeSystem.MainModule, currentDecompileRun.Namespaces);

			EntityDeclaration propertyDecl = typeSystemAstBuilder.ConvertEntity(tsProperty);
			if (tsProperty.IsExplicitInterfaceImplementation && !tsProperty.IsIndexer) {
				int lastDot = tsProperty.Name.LastIndexOf('.');
				propertyDecl.NameToken.Name = tsProperty.Name.Substring(lastDot + 1);
			}

			FixParameterNames(propertyDecl);
			Accessor getter, setter;
			if (propertyDecl is PropertyDeclaration propertyDeclaration) {
				getter = propertyDeclaration.Getter;
				setter = propertyDeclaration.Setter;
			} else {
				getter = ((IndexerDeclaration)propertyDecl).Getter;
				setter = ((IndexerDeclaration)propertyDecl).Setter;
			}

			bool getterHasBody = tsProperty.CanGet && tsProperty.Getter!.HasBody;
			bool setterHasBody = tsProperty.CanSet && tsProperty.Setter!.HasBody;
			if (getterHasBody) {
				AddMethodBody(getter, propertyDef.GetMethod, tsProperty.Getter, currentTypeResolveContext.WithCurrentMember(tsProperty), null, false, MethodKind.Property);
			}
			if (setterHasBody) {
				AddMethodBody(setter, propertyDef.SetMethod, tsProperty.Setter, currentTypeResolveContext.WithCurrentMember(tsProperty), null, true, MethodKind.Property);
			}
			if (!getterHasBody && !setterHasBody && !tsProperty.IsAbstract && tsProperty.DeclaringType.Kind != TypeKind.Interface) {
				propertyDecl.Modifiers |= Modifiers.Extern;
			}
			var accessor = propertyDef.GetMethod ?? propertyDef.SetMethod;
			if (!accessor.HasOverrides && accessor.IsVirtual == accessor.IsNewSlot)
			{
				SetNewModifier(propertyDecl);
			}
			if (getterHasBody && IsCovariantReturnOverride(tsProperty.Getter))
			{
				RemoveAttribute(getter, KnownAttribute.PreserveBaseOverrides);
				propertyDecl.Modifiers &= ~(Modifiers.New | Modifiers.Virtual);
				propertyDecl.Modifiers |= Modifiers.Override;
			}
			if (context.Settings.RequiredMembers && RemoveAttribute(propertyDecl, KnownAttribute.RequiredAttribute))
			{
				propertyDecl.Modifiers |= Modifiers.Required;
			}

			if (propertyDef.SetMethod != null)
				AddComment(propertyDecl, propertyDef.SetMethod, "set");
			if (propertyDef.GetMethod != null)
				AddComment(propertyDecl, propertyDef.GetMethod, "get");
			AddComment(propertyDecl, propertyDef);

			return propertyDecl;
		}

		private EntityDeclaration CreateEvent(EventDef eventDef)
		{
			var tsEvent = typeSystem.MainModule.GetDefinition(eventDef);

			RequiredNamespaceCollector.CollectNamespaces(tsEvent, typeSystem.MainModule, currentDecompileRun.Namespaces);

			bool adderHasBody = tsEvent.CanAdd && tsEvent.AddAccessor!.HasBody;
			bool removerHasBody = tsEvent.CanRemove && tsEvent.RemoveAccessor!.HasBody;
			typeSystemAstBuilder.UseCustomEvents = tsEvent.DeclaringTypeDefinition!.Kind != TypeKind.Interface
												   || tsEvent.IsExplicitInterfaceImplementation
												   || adderHasBody
												   || removerHasBody;
			var eventDecl = typeSystemAstBuilder.ConvertEntity(tsEvent);
			if (tsEvent.IsExplicitInterfaceImplementation) {
				int lastDot = tsEvent.Name.LastIndexOf('.');
				eventDecl.NameToken.Name = tsEvent.Name.Substring(lastDot + 1);
			}

			if (adderHasBody)
			{
				AddMethodBody(((CustomEventDeclaration)eventDecl).AddAccessor, eventDef.AddMethod, tsEvent.AddAccessor,
					currentTypeResolveContext.WithCurrentMember(tsEvent), null, true, MethodKind.Event);
			}
			if (removerHasBody)
			{
				AddMethodBody(((CustomEventDeclaration)eventDecl).RemoveAccessor, eventDef.RemoveMethod, tsEvent.RemoveAccessor,
					currentTypeResolveContext.WithCurrentMember(tsEvent), null, true, MethodKind.Event);
			}
			if (!adderHasBody && !removerHasBody && !tsEvent.IsAbstract && tsEvent.DeclaringType.Kind != TypeKind.Interface)
			{
				eventDecl.Modifiers |= Modifiers.Extern;
			}
			var accessor = eventDef.AddMethod ?? eventDef.RemoveMethod;
			if (accessor.IsVirtual == accessor.IsNewSlot) {
				SetNewModifier(eventDecl);
			}

			if (eventDef.RemoveMethod != null)
				AddComment(eventDecl, eventDef.RemoveMethod, "remove");
			if (eventDef.AddMethod != null)
				AddComment(eventDecl, eventDef.AddMethod, "add");
			AddComment(eventDecl, eventDef);

			return eventDecl;
		}

		private void AddMethodBody(EntityDeclaration methodNode, MethodDef method, Decompiler.TypeSystem.IMethod tsMethod, ITypeResolveContext typeResolveContext,
			IEnumerable<ParameterDeclaration> parameters, bool valueParameterIsKeyword, MethodKind methodKind)
		{
			ClearCurrentMethodState();
			if (method.Body is null)
				return;

			var bodyKind = GetDecompiledBodyKind?.Invoke(this, method) ?? DecompiledBodyKind.Full;
			// In order for auto events to be optimized from custom to auto events, they must have bodies.
			// DecompileTypeMethodsTransform has a fix to remove the hidden custom events' bodies.
			if (bodyKind == DecompiledBodyKind.Empty && methodKind == MethodKind.Event)
				bodyKind = DecompiledBodyKind.Full;

			switch (bodyKind) {
				case DecompiledBodyKind.Full:
					MethodDebugInfoBuilder builder3;
					BlockStatement bs;
					try {
						if (context.AsyncMethodBodyDecompilation) {
							var clonedContext = this.context.Clone();
							var bodyTask = Task.Run(() => {
								if (clonedContext.CancellationToken.IsCancellationRequested)
									return default;
								var asyncState = GetAsyncMethodBodyDecompilationState();
								var sb = asyncState.StringBuilder;
								BlockStatement body;
								MethodDebugInfoBuilder builder2;
								ILFunction ilFunction = null;
								try {
									body = CSharpAstMethodBodyBuilder.DecompileMethodBody(method, tsMethod, currentDecompileRun, typeResolveContext, typeSystem, clonedContext, sb, methodNode, out builder2, out ilFunction);
								}
								catch (OperationCanceledException) {
									throw;
								}
								catch (Exception ex) {
									CreateBadMethod(method, ex, sb, out body, out builder2);
								}
								Return(asyncState);
								return new AsyncMethodBodyResult(methodNode, method, body, builder2, ilFunction);
							}, clonedContext.CancellationToken);
							methodBodyTasks.Add(bodyTask);
						}
						else {
							var body = CSharpAstMethodBodyBuilder.DecompileMethodBody(method, tsMethod, currentDecompileRun, typeResolveContext, typeSystem, context, stringBuilder, methodNode, out var builder, out var ilFunction);
							methodNode.AddChild(body, Roles.Body);
							methodNode.WithAnnotation(builder);
							AddDefinesForConditionalAttributes(ilFunction);
							CleanUpMethodDeclaration(methodNode, body, ilFunction);
						}
						return;
					}
					catch (OperationCanceledException) {
						throw;
					}
					catch (Exception ex) {
						CreateBadMethod(method, ex, stringBuilder, out bs, out builder3);
					}
					methodNode.AddChild(bs, Roles.Body);
					methodNode.WithAnnotation(builder3);
					return;

				case DecompiledBodyKind.Empty:
					CSharpAstMethodBodyBuilder.DecompileEmptyBody(methodNode, method, tsMethod, typeSystem, typeSystemAstBuilder, parameters);
					return;

				case DecompiledBodyKind.None:
					return;

				default:
					throw new InvalidOperationException();
			}
		}

		private void CreateBadMethod(MethodDef method, Exception ex, StringBuilder sb, out BlockStatement bs, out MethodDebugInfoBuilder builder)
		{
			sb.Clear();
			sb.Append(Environment.NewLine);
			sb.Append("An exception occurred when decompiling this method (");
			// Don't use Append(object) to avoid boxing.
			sb.Append(method.MDToken.ToString());
			sb.Append(')');
			sb.Append(Environment.NewLine);
			sb.Append(Environment.NewLine);
			sb.Append(ex);
			sb.Append(Environment.NewLine);

			bs = new BlockStatement();
			var emptyStmt = new EmptyStatement();
			emptyStmt.AddAnnotation(new List<ILSpan>(1) { new ILSpan(0, (uint)method.Body.GetCodeSize()) });
			bs.Statements.Add(emptyStmt);
			bs.InsertChildAfter(null, new Comment(sb.ToString(), CommentType.MultiLine), Roles.Comment);
			builder = new MethodDebugInfoBuilder(context.SettingsVersion, StateMachineKind.None, method, null,
				method.Body.Variables.Select(a => new SourceLocal(a, CreateLocalName(a), a.Type, SourceVariableFlags.None))
					  .ToArray(), method.Parameters.Select(a => new SourceParameter(a, a.Name, a.Type, SourceVariableFlags.None)).ToArray(), null);

			static string CreateLocalName(Local local) {
				var name = local.Name;
				return string.IsNullOrEmpty(name) ? $"V_{local.Index}" : name;
			}
		}

		public void RunTransformations(Predicate<IAstTransform> transformAbortCondition = null)
		{
			WaitForBodies();

			RunTransforms(transformAbortCondition);
			transformationsHaveRun = true;
		}

		private void RunTransforms(Predicate<IAstTransform> transformAbortCondition)
		{
			var transformContext = new TransformContext(typeSystem, currentDecompileRun, currentTypeResolveContext, typeSystemAstBuilder, stringBuilder);

			CSharpTransformationPipeline.RunTransformationsUntil(syntaxTree, transformAbortCondition, context, transformContext);

			context.CancellationToken.ThrowIfCancellationRequested();
			syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = context.Settings.InsertParenthesesForReadability });
			context.CancellationToken.ThrowIfCancellationRequested();
			GenericGrammarAmbiguityVisitor.ResolveAmbiguities(syntaxTree);
		}

		public void GenerateCode(IDecompilerOutput output)
		{
			if (!transformationsHaveRun)
				RunTransformations();

			var outputFormatter = new TextTokenWriter(output, context);
			var formattingPolicy = context.Settings.CSharpFormattingOptions;
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(outputFormatter, formattingPolicy, context.CancellationToken));
		}

		private static char ToHexChar(int val) {
			Debug.Assert(0 <= val && val <= 0x0F);
			if (0 <= val && val <= 9)
				return (char)('0' + val);
			return (char)('A' + val - 10);
		}

		private string ToHex(uint value) {
			commentBuffer[0] = '0';
			commentBuffer[1] = 'x';
			int j = 2;
			for (int i = 0; i < 4; i++) {
				commentBuffer[j++] = ToHexChar((int)(value >> 28) & 0x0F);
				commentBuffer[j++] = ToHexChar((int)(value >> 24) & 0x0F);
				value <<= 8;
			}
			return new string(commentBuffer, 0, j);
		}

		private void AddComment(AstNode node, IMemberDef member, string text = null)
		{
			if (!this.context.Settings.ShowTokenAndRvaComments)
				return;
			member.GetRVA(out uint rva, out long fileOffset);

			var creator = new CommentReferencesCreator(stringBuilder);
			creator.AddText(" ");
			if (text != null) {
				creator.AddText("(");
				creator.AddText(text);
				creator.AddText(") ");
			}
			creator.AddText("Token: ");
			creator.AddReference(ToHex(member.MDToken.Raw), new TokenReference(member));
			creator.AddText(" RID: ");
			creator.AddText(member.MDToken.Rid.ToString());
			if (rva != 0) {
				var filename = member.Module?.Location;
				creator.AddText(" RVA: ");
				creator.AddReference(ToHex(rva), new AddressReference(filename, true, rva, 0));
				creator.AddText(" File Offset: ");
				creator.AddReference(ToHex((uint)fileOffset), new AddressReference(filename, false, (ulong)fileOffset, 0));
			}

			var cmt = new Comment(creator.Text) {
				References = creator.CommentReferences
			};
			node.InsertChildAfter(null, cmt, Roles.Comment);
		}
	}
}
