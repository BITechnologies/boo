#region license
// boo - an extensible programming language for the CLI
// Copyright (C) 2004 Rodrigo B. de Oliveira
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
//
// As a special exception, if you link this library with other files to
// produce an executable, this library does not by itself cause the
// resulting executable to be covered by the GNU General Public License.
// This exception does not however invalidate any other reasons why the
// executable file might be covered by the GNU General Public License.
//
// Contact Information
//
// mailto:rbo@acm.org
#endregion

using System;
using System.Collections;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Boo.Lang.Ast;
using Boo.Lang.Compiler;
using Boo.Lang.Compiler.Bindings;

namespace Boo.Lang.Compiler.Pipeline
{
	public class EmitAssemblyStep : AbstractSwitcherCompilerStep
	{		
		public static MethodInfo GetEntryPoint(CompileUnit cu)
		{
			return (MethodInfo)cu[EntryPointKey];
		}
		
		public static AssemblyBuilder GetAssemblyBuilder(CompilerContext context)
		{
			AssemblyBuilder builder = (AssemblyBuilder)context.CompileUnit[AssemblyBuilderKey];
			if (null == builder)
			{
				throw CompilerErrorFactory.InvalidAssemblySetUp(context.CompileUnit);
			}
			return builder;
		}
		
		public static ModuleBuilder GetModuleBuilder(CompilerContext context)
		{
			ModuleBuilder builder = (ModuleBuilder)context.CompileUnit[ModuleBuilderKey];
			if (null == builder)
			{
				throw CompilerErrorFactory.InvalidAssemblySetUp(context.CompileUnit);
			}
			return builder;
		}
		
		static object EntryPointKey = new object();
		
		static object AssemblyBuilderKey = new object();
		
		static object ModuleBuilderKey = new object();
		
		static MethodInfo String_Format = typeof(string).GetMethod("Format", new Type[] { Types.String, Types.ObjectArray });
		
		static MethodInfo String_Format1 = typeof(string).GetMethod("Format", new Type[] { Types.String, Types.Object });
		
		static MethodInfo String_Format2 = typeof(string).GetMethod("Format", new Type[] { Types.String, Types.Object, Types.Object });
		
		static MethodInfo String_Format3 = typeof(string).GetMethod("Format", new Type[] { Types.String, Types.Object, Types.Object, Types.Object });
		
		static MethodInfo RuntimeServices_MoveNext = Types.RuntimeServices.GetMethod("MoveNext");
		
		static MethodInfo RuntimeServices_CheckArrayUnpack = Types.RuntimeServices.GetMethod("CheckArrayUnpack");
		
		static MethodInfo RuntimeServices_NormalizeArrayIndex = Types.RuntimeServices.GetMethod("NormalizeArrayIndex");
		
		static MethodInfo RuntimeServices_GetEnumerable = Types.RuntimeServices.GetMethod("GetEnumerable");
		
		static MethodInfo IEnumerable_GetEnumerator = Types.IEnumerable.GetMethod("GetEnumerator");
		
		static MethodInfo IEnumerator_MoveNext = Types.IEnumerator.GetMethod("MoveNext");
		
		static MethodInfo IEnumerator_get_Current = Types.IEnumerator.GetProperty("Current").GetGetMethod();
		
		static ConstructorInfo List_EmptyConstructor = Types.List.GetConstructor(Type.EmptyTypes);
		
		static ConstructorInfo List_IntConstructor = Types.List.GetConstructor(new Type[] { typeof(int) });
		
		static ConstructorInfo Object_Constructor = Types.Object.GetConstructor(new Type[0]);
		
		static MethodInfo List_Add = Types.List.GetMethod("Add", new Type[] { Types.Object });
		
		static MethodInfo Type_GetTypeFromHandle = Types.Type.GetMethod("GetTypeFromHandle");
		
		static Type[] DelegateConstructorTypes = new Type[] { Types.Object, Types.IntPtr };
		
		AssemblyBuilder _asmBuilder;
		
		ModuleBuilder _moduleBuilder;
		
		ISymbolDocumentWriter _symbolDocWriter;
		
		TypeBuilder _typeBuilder;
		
		// IL generation state		
		ILGenerator _il;
		Label _returnLabel; // current label for method return
		LocalBuilder _returnValueLocal; // returnValueLocal
		ITypeBinding _returnType;
		int _tryBlock; // are we in a try block?
		Hashtable _typeCache = new Hashtable();
		
		// keeps track of types on the IL stack
		System.Collections.Stack _types = new System.Collections.Stack();
		
		void PushType(ITypeBinding type)
		{
			_types.Push(type);
		}
		
		void PushVoid()
		{
			PushType(BindingManager.VoidTypeBinding);
		}
		
		ITypeBinding PopType()
		{
			return (ITypeBinding)_types.Pop();
		}
		
		ITypeBinding PeekTypeOnStack()
		{
			return (ITypeBinding)_types.Peek();
		}
		
		void AssertStackIsEmpty(string message)
		{
			if (0 != _types.Count)
			{
				throw new ApplicationException(
						string.Format("{0}: {1} items still on the stack.", message, _types.Count)
						);
			}
		}
		
		public override void Run()
		{				
			if (Errors.Count > 0 || 0 == CompileUnit.Modules.Count)
			{
				return;				
			}
			
			SetUpAssembly();
			
			ArrayList types = CollectTypes();
			
			foreach (TypeDefinition type in types)
			{
				DefineType(type);
			}
			
			foreach (TypeDefinition type in types)
			{
				DefineTypeMembers(type);
			}
			
			foreach (Boo.Lang.Ast.Module module in CompileUnit.Modules)
			{
				OnModule(module);
			}
			
			CreateTypes(types);
			
			DefineEntryPoint();			
		}
		
		void CreateTypes(ArrayList types)
		{
			Hashtable created = new Hashtable();
			foreach (TypeDefinition type in types)
			{
				CreateType(created, type);
			}
		}
		
		void CreateType(Hashtable created, TypeDefinition type)
		{				
			if (!created.ContainsKey(type))
			{
				created.Add(type, type);
				foreach (TypeReference baseTypeRef in type.BaseTypes)
				{
					InternalTypeBinding binding = GetBoundType(baseTypeRef) as InternalTypeBinding;
					if (null != binding)
					{
						CreateType(created, binding.TypeDefinition);
					}
				}				
				GetTypeBuilder(type).CreateType();
			}
		}
		
		ArrayList CollectTypes()
		{
			ArrayList types = new ArrayList();
			foreach (Boo.Lang.Ast.Module module in CompileUnit.Modules)
			{				 
				CollectTypes(types, module.Members);
			}
			return types;
		}
		
		void CollectTypes(ArrayList types, TypeMemberCollection members)
		{
			foreach (TypeMember member in members)
			{
				switch (member.NodeType)
				{
					case NodeType.ClassDefinition:
					{
						types.Add(member);
						CollectTypes(types, ((TypeDefinition)member).Members);
						break;
					}
				}
			}
		}
		
		public override void Dispose()
		{
			base.Dispose();
			
			_asmBuilder = null;		
			_moduleBuilder = null;		
			_symbolDocWriter = null;
			_typeBuilder = null;
			_il = null;		
			_returnValueLocal = null;
			_returnType = null;
			_tryBlock = 0;
			_types.Clear();
			_typeCache.Clear();
		}
		
		public override void OnAttribute(Boo.Lang.Ast.Attribute node)
		{
		}
		
		public override void OnModule(Boo.Lang.Ast.Module module)
		{			
			_symbolDocWriter = _moduleBuilder.DefineDocument(module.LexicalInfo.FileName, Guid.Empty, Guid.Empty, Guid.Empty);			
			module.Members.Switch(this);
		}
		
		public override void OnClassDefinition(ClassDefinition node)
		{
			EmitTypeDefinition(node);
		}
		
		void EmitTypeDefinition(TypeDefinition node)
		{
			TypeBuilder current = GetTypeBuilder(node);
			EmitBaseTypesAndAttributes(node, current);			
			node.Members.Switch(this);
			
			_typeBuilder = current;
		}		
		
		public override void OnMethod(Method method)
		{			
			MethodBuilder methodBuilder = GetMethodBuilder(method);			
			_il = methodBuilder.GetILGenerator();
			_returnLabel = _il.DefineLabel();
			
			_returnType = ((IMethodBinding)GetBinding(method)).ReturnType;
			if (BindingManager.VoidTypeBinding != _returnType)
			{
				_returnValueLocal = _il.DeclareLocal(GetType(_returnType));
			}
			
			method.Locals.Switch(this);
			method.Body.Switch(this);
			
			_il.MarkLabel(_returnLabel);
			
			if (null != _returnValueLocal)
			{
				_il.Emit(OpCodes.Ldloc, _returnValueLocal);
				_returnValueLocal = null;
			}
			_il.Emit(OpCodes.Ret);			
		}
		
		public override void OnConstructor(Constructor constructor)
		{
			ConstructorBuilder builder = GetConstructorBuilder(constructor);
			_il = builder.GetILGenerator();

			InternalConstructorBinding binding = (InternalConstructorBinding)GetBinding(constructor);
			constructor.Locals.Switch(this);
			constructor.Body.Switch(this);
			_il.Emit(OpCodes.Ret);
		}
		
		public override void OnLocal(Local local)
		{			
			LocalBinding info = GetLocalBinding(local);
			info.LocalBuilder = _il.DeclareLocal(GetType(local));
			info.LocalBuilder.SetLocalSymInfo(local.Name);			
		}
		
		public override void OnForStatement(ForStatement node)
		{									
			EmitDebugInfo(node, node.Iterator);
			
			// iterator = <node.Iterator>;
			node.Iterator.Switch(this);		
			
			ITypeBinding iteratorType = PopType();
			if (iteratorType.IsArray)
			{
				EmitArrayBasedFor(node, iteratorType);
			}
			else
			{
				EmitEnumerableBasedFor(node, iteratorType);
			}			
		}
		
		public override void OnReturnStatement(ReturnStatement node)
		{
			OpCode retOpCode = _tryBlock > 0 ? OpCodes.Leave : OpCodes.Br;
			
			if (null != node.Expression)
			{
				Switch(node.Expression);
				EmitCastIfNeeded(_returnType, PopType());
				_il.Emit(OpCodes.Stloc, _returnValueLocal);
			}
			_il.Emit(retOpCode, _returnLabel);
		}
		
		public override void OnRaiseStatement(RaiseStatement node)
		{
			Switch(node.Exception); PopType();
			_il.Emit(OpCodes.Throw);
		}
		
		public override void OnTryStatement(TryStatement node)
		{
			++_tryBlock;
			
			Label endLabel = _il.BeginExceptionBlock();
			Switch(node.ProtectedBlock);
			Switch(node.ExceptionHandlers);
			if (null != node.EnsureBlock)
			{
				_il.BeginFinallyBlock();
				Switch(node.EnsureBlock);
			}
			_il.EndExceptionBlock();
			
			--_tryBlock;
		}
		
		public override void OnExceptionHandler(ExceptionHandler node)
		{
			_il.BeginCatchBlock(GetType(node.Declaration));
			_il.Emit(OpCodes.Stloc, GetLocalBuilder(node.Declaration));
			Switch(node.Block);
		}
		
		public override void OnUnpackStatement(UnpackStatement node)
		{
			DeclarationCollection decls = node.Declarations;
			
			EmitDebugInfo(decls[0], node.Expression);						
			node.Expression.Switch(this);
			
			EmitUnpackForDeclarations(node.Declarations, PopType());			
		}	
		
		public override bool EnterExpressionStatement(ExpressionStatement node)
		{
			EmitDebugInfo(node);
			return true;
		}
		
		public override void LeaveExpressionStatement(ExpressionStatement node)
		{					
			// if the type of the inner expression is not
			// void we need to pop its return value to leave
			// the stack sane
			if (PopType() != BindingManager.VoidTypeBinding)
			{				
				_il.Emit(OpCodes.Pop);				
			}
			AssertStackIsEmpty("stack must be empty after a statement!");
		}
		
		public override void OnIfStatement(IfStatement node)
		{
			EmitDebugInfo(node, node.Expression);
			
			Label endLabel = _il.DefineLabel();
			
			EmitBranchFalse(node.Expression, endLabel);
			node.TrueBlock.Switch(this);
			if (null != node.FalseBlock)
			{
				Label elseEndLabel = _il.DefineLabel();
				_il.Emit(OpCodes.Br, elseEndLabel);
				_il.MarkLabel(endLabel);
				
				endLabel = elseEndLabel;
				node.FalseBlock.Switch(this);
			}
			
			_il.MarkLabel(endLabel);
		}
		
		void EmitBranchTrue(Expression expression, Label label)
		{
			expression.Switch(this); PopType();
			_il.Emit(OpCodes.Brtrue, label);
		}
		
		void EmitBranchFalse(Expression expression, Label label)
		{
			switch (expression.NodeType)
			{
				case NodeType.UnaryExpression:
				{
					EmitBranchFalse((UnaryExpression)expression, label);
					break;
				}
				
				default:
				{
					DefaultBranchFalse(expression, label);
					break;
				}
			}
		}
		
		void EmitBranchFalse(UnaryExpression expression, Label label)
		{
			switch (expression.Operator)
			{
				case UnaryOperatorType.Not:
				{
					EmitBranchTrue(expression.Operand, label);
					break;
				}
				
				default:					
				{		
					DefaultBranchFalse(expression, label);
					break;
				}
			}
		}
		
		void DefaultBranchFalse(Expression expression, Label label)
		{
			expression.Switch(this); PopType();
			_il.Emit(OpCodes.Brfalse, label);
		}
		
		public override void OnUnaryExpression(UnaryExpression node)
		{
			switch (node.Operator)
			{
				case UnaryOperatorType.Not:
				{
					node.Operand.Switch(this); PopType();
					
					// bool codification:
					// value_on_stack ? 1 : 0
					Label wasTrue = _il.DefineLabel();
					Label wasFalse = _il.DefineLabel();
					_il.Emit(OpCodes.Brfalse, wasFalse);
					_il.Emit(OpCodes.Ldc_I4_0);
					_il.Emit(OpCodes.Br, wasTrue);
					_il.MarkLabel(wasFalse);
					_il.Emit(OpCodes.Ldc_I4_1);
					_il.MarkLabel(wasTrue);
					
					PushType(BindingManager.BoolTypeBinding);
					break;
				}
				
				default:
				{
					Errors.Add(CompilerErrorFactory.NotImplemented(node, "unary operator not supported"));
					break;
				}
			}
		}
		
		bool ShouldLeaveValueOnStack(Expression node)
		{
			return node.ParentNode.NodeType != NodeType.ExpressionStatement;
		}
		
		void OnReferenceComparison(BinaryExpression node)
		{
			node.Left.Switch(this); PopType();
			node.Right.Switch(this); PopType();
			_il.Emit(OpCodes.Ceq);
			if (BinaryOperatorType.ReferenceInequality == node.Operator)
			{
				_il.Emit(OpCodes.Ldc_I4_0);
				_il.Emit(OpCodes.Ceq);
			}
			PushType(BindingManager.BoolTypeBinding);
		}
		
		void OnAssignment(BinaryExpression node)
		{
			// when the parent is not a statement we need to leave
			// the value on the stack
			bool leaveValueOnStack = ShouldLeaveValueOnStack(node);				
			IBinding binding = BindingManager.GetBinding(node.Left);
			switch (binding.BindingType)
			{
				case BindingType.Local:
				{
					SetLocal(node, (LocalBinding)binding, leaveValueOnStack);
					break;
				}
				
				case BindingType.Property:
				{
					IPropertyBinding property = (IPropertyBinding)binding;						
					SetProperty(node, property, node.Left, node.Right, leaveValueOnStack);
					break;
				}
				
				case BindingType.Field:
				{
					IFieldBinding field = (IFieldBinding)binding;
					SetField(node, field, node.Left, node.Right, leaveValueOnStack);
					break;
				}
					
				default:
				{
					Errors.Add(CompilerErrorFactory.NotImplemented(node, binding.ToString()));
					break;
				}
			}		
			if (!leaveValueOnStack)
			{				
				PushVoid();
			}
		}
		
		public override void OnBinaryExpression(BinaryExpression node)
		{				
			switch (node.Operator)
			{
				case BinaryOperatorType.Assign:
				{
					OnAssignment(node);
					break;
				}
				
				case BinaryOperatorType.ReferenceInequality:
				{
					OnReferenceComparison(node);
					break;
				}
				
				case BinaryOperatorType.ReferenceEquality:
				{
					OnReferenceComparison(node);
					break;
				}
				
				case BinaryOperatorType.InPlaceAdd:
				{
					Switch(((MemberReferenceExpression)node.Left).Target); PopType();
					AddDelegate(node, GetBinding(node.Left), node.Right);
					PushVoid();
					break;
				}
				
				default:
				{				
					IBinding binding = BindingManager.GetBinding(node);
					if (BindingType.Method == binding.BindingType)
					{
						// operator
						IMethodBinding methodBinding = (IMethodBinding)binding;
						node.Left.Switch(this);
						EmitCastIfNeeded(methodBinding.GetParameterType(0), PopType());
						node.Right.Switch(this);
						EmitCastIfNeeded(methodBinding.GetParameterType(1), PopType());
						_il.EmitCall(OpCodes.Call, GetMethodInfo(methodBinding), null);
						PushType(methodBinding.ReturnType);
					}
					else
					{
						Errors.Add(CompilerErrorFactory.NotImplemented(node, binding.ToString()));
					}
					break;
				}
			}
		}
		
		public override void OnAsExpression(AsExpression node)
		{
			Type type = GetType(node.Type);
			
			node.Target.Switch(this); PopType();			
			_il.Emit(OpCodes.Isinst, type);
			PushType(GetBoundType(node));
		}
		
		void InvokeMethod(IMethodBinding methodBinding, MethodInvocationExpression node)
		{			
			MethodInfo mi = GetMethodInfo(methodBinding);
			OpCode code = OpCodes.Call;
			if (!mi.IsStatic)
			{
				// pushes target reference
				node.Target.Switch(this);
				
				ITypeBinding targetType = PopType();
				
				bool declaringTypeIsValueType = mi.DeclaringType.IsValueType;
				bool targetTypeIsValueType = targetType.IsValueType; 
				if (!declaringTypeIsValueType &&
					!targetTypeIsValueType)
				{
					if (mi.IsVirtual)
					{
						code = OpCodes.Callvirt;
					}
				}
				else
				{
					if (declaringTypeIsValueType)
					{
						// declare local to hold value type
						LocalBuilder temp = _il.DeclareLocal(GetType(targetType));
						_il.Emit(OpCodes.Stloc, temp);
						_il.Emit(OpCodes.Ldloca, temp);
					}
					else
					{
						_il.Emit(OpCodes.Box, GetType(targetType));
					}
				}
			}
			PushArguments(methodBinding, node.Arguments);
			_il.EmitCall(code, mi, null);
			
			PushType(methodBinding.ReturnType);
		}
		
		void InvokeSuperMethod(IMethodBinding methodBinding, MethodInvocationExpression node)
		{
			IMethodBinding super = ((InternalMethodBinding)methodBinding).Override;
			MethodInfo superMI = GetMethodInfo(super);
			_il.Emit(OpCodes.Ldarg_0); // this
			PushArguments(super, node.Arguments);
			_il.EmitCall(OpCodes.Call, superMI, null);
			PushType(super.ReturnType);
		}
		
		void OnSpecialFunction(IBinding binding, MethodInvocationExpression node)
		{
			EmitGetTypeFromHandle(GetType(node.Arguments[0]));
		}
		
		void EmitGetTypeFromHandle(Type type)
		{
			_il.Emit(OpCodes.Ldtoken, type);
			_il.EmitCall(OpCodes.Call, Type_GetTypeFromHandle, null);
			PushType(BindingManager.TypeTypeBinding);
		}
		
		public override void OnMethodInvocationExpression(MethodInvocationExpression node)
		{				
			IBinding binding = BindingManager.GetBinding(node.Target);
			switch (binding.BindingType)
			{
				case BindingType.SpecialFunction:
				{
					OnSpecialFunction(binding, node);
					break;
				}
				
				case BindingType.Method:
				{	
					IMethodBinding methodBinding = (IMethodBinding)binding;
					
					if (node.Target.NodeType == NodeType.SuperLiteralExpression)
					{
						InvokeSuperMethod(methodBinding, node);
					}
					else
					{						
						InvokeMethod(methodBinding, node);
					}
					
					break;
				}
				
				case BindingType.Constructor:
				{
					IConstructorBinding constructorBinding = (IConstructorBinding)binding;
					ConstructorInfo ci = GetConstructorInfo(constructorBinding);
					
					if (NodeType.SuperLiteralExpression == node.Target.NodeType)
					{
						// super constructor call
						_il.Emit(OpCodes.Ldarg_0);
						PushArguments(constructorBinding, node.Arguments);
						_il.Emit(OpCodes.Call, ci);
						PushVoid();
					}
					else
					{
						PushArguments(constructorBinding, node.Arguments);
						_il.Emit(OpCodes.Newobj, ci);
						foreach (ExpressionPair pair in node.NamedArguments)
						{
							// object reference
							_il.Emit(OpCodes.Dup);
							
							IBinding memberBinding = BindingManager.GetBinding(pair.First);						
							// field/property reference						
							InitializeMember(node, memberBinding, pair.Second);
						}
						
						// constructor invocation resulting type is
						PushType(constructorBinding.DeclaringType);
					}
					break;
				}
				
				default:
				{
					Errors.Add(CompilerErrorFactory.NotImplemented(node, binding.ToString()));
					break;
				}
			}
		}
		
		public override void OnIntegerLiteralExpression(IntegerLiteralExpression node)
		{
			_il.Emit(OpCodes.Ldc_I4, (int)node.Value);
			PushType(BindingManager.IntTypeBinding);
		}
		
		public override void OnBoolLiteralExpression(BoolLiteralExpression node)
		{
			if (node.Value)
			{
				_il.Emit(OpCodes.Ldc_I4_1);
			}
			else
			{
				_il.Emit(OpCodes.Ldc_I4_0);
			}
			PushType(BindingManager.BoolTypeBinding);
		}
		
		public override void OnListLiteralExpression(ListLiteralExpression node)
		{
			if (node.Items.Count > 0)
			{
				_il.Emit(OpCodes.Ldc_I4, node.Items.Count);
				_il.Emit(OpCodes.Newobj, List_IntConstructor);
				
				foreach (Expression item in node.Items)
				{					
					item.Switch(this);
					EmitCastIfNeeded(BindingManager.ObjectTypeBinding, PopType());
					_il.EmitCall(OpCodes.Call, List_Add, null);
					// List_Add will return the list itself
				}
			}
			else
			{
				_il.Emit(OpCodes.Newobj, List_EmptyConstructor);			
			}
			PushType(BindingManager.ListTypeBinding);
		}
		
		public override void OnTupleLiteralExpression(TupleLiteralExpression node)
		{
			EmitObjectArray(node.Items);
			PushType(GetBoundType(node));
		}
		
		public override void OnStringLiteralExpression(StringLiteralExpression node)
		{
			_il.Emit(OpCodes.Ldstr, node.Value);
			PushType(BindingManager.StringTypeBinding);
		}
		
		public override void OnSlicingExpression(SlicingExpression node)
		{
			Switch(node.Target); 			
			ITypeBinding type = PopType();

			bool isNegative = false;
			if (CanBeNegative(node.Begin, ref isNegative))
			{					
				if (isNegative)
				{					
					_il.Emit(OpCodes.Dup);
					_il.Emit(OpCodes.Ldlen);
					LoadInt(node.Begin);
					_il.Emit(OpCodes.Add);
				}
				else
				{
					_il.Emit(OpCodes.Dup);
					LoadInt(node.Begin);
					_il.EmitCall(OpCodes.Call, RuntimeServices_NormalizeArrayIndex, null);
				}
			}
			else
			{
				LoadInt(node.Begin);
			}
			_il.Emit(OpCodes.Ldelem_Ref);
			
			PushType(type.GetElementType());
		}
		
		bool CanBeNegative(Expression expression, ref bool isNegative)
		{
			IntegerLiteralExpression integer = expression as IntegerLiteralExpression;
			if (null != integer)
			{
				if (integer.Value >= 0)
				{
					return false;
				}
				isNegative = true;
			}
			return true;
		}
		
		void LoadInt(Expression expression)
		{
			Switch(expression);
			EmitCastIfNeeded(BindingManager.IntTypeBinding, PopType());
		}
		
		public override void OnStringFormattingExpression(StringFormattingExpression node)
		{			              
			_il.Emit(OpCodes.Ldstr, node.Template);
			
			int argc = node.Arguments.Count;
			if (argc > 3)
			{
				// new object[node.Arguments.Count]
				EmitObjectArray(node.Arguments);
				_il.EmitCall(OpCodes.Call, String_Format, null);
			}
			else
			{
				for (int i=0; i<argc; ++i)
				{
					node.Arguments[i].Switch(this);
					EmitCastIfNeeded(BindingManager.ObjectTypeBinding, PopType());
				}
				
				switch (argc)
				{
					case 1:
					{
						_il.EmitCall(OpCodes.Call, String_Format1, null);
						break;
					}
					
					case 2:
					{
						_il.EmitCall(OpCodes.Call, String_Format2, null);
						break;
					}
					
					case 3:
					{
						_il.EmitCall(OpCodes.Call, String_Format3, null);
						break;
					}
				}
			}
			PushType(BindingManager.StringTypeBinding);
		}
		
		public override void OnMemberReferenceExpression(MemberReferenceExpression node)
		{			
			IBinding binding = BindingManager.GetBinding(node);
			switch (binding.BindingType)
			{
				case BindingType.Property:
				{
					OpCode code = OpCodes.Call;
					PropertyInfo property = GetPropertyInfo(binding);
					MethodInfo getMethod = property.GetGetMethod(true);
					if (!getMethod.IsStatic)
					{	
						ITypeBinding targetType = GetBoundType(node.Target);
						if (targetType.IsValueType)
						{
							LoadAddress(node.Target, targetType);
						}
						else
						{
							node.Target.Switch(this); PopType();
						}
						if (getMethod.IsVirtual)
						{
							code = OpCodes.Callvirt;
						}
					}
					_il.EmitCall(code, getMethod, null);					
					PushType(((ITypedBinding)binding).BoundType);
					break;
				}
				
				case BindingType.Method:
				{
					node.Target.Switch(this);
					break;
				}
				
				case BindingType.Field:
				{
					IFieldBinding fieldBinding = (IFieldBinding)binding;
					FieldInfo fieldInfo = GetFieldInfo(fieldBinding);
					if (fieldBinding.IsStatic)
					{
						if (fieldInfo.DeclaringType.IsEnum)
						{
							_il.Emit(OpCodes.Ldc_I4, (int)GetFieldInfo(fieldBinding).GetValue(null));							
						}
						else
						{
							_il.Emit(OpCodes.Ldsfld, fieldInfo);							
						}
					}
					else
					{						
						node.Target.Switch(this); PopType();
						_il.Emit(OpCodes.Ldfld, fieldInfo);						
					}
					PushType(fieldBinding.BoundType);
					break;
				}
				
				case BindingType.TypeReference:
				{
					EmitGetTypeFromHandle(GetType(node));
					break;
				}
				
				default:
				{
					Errors.Add(CompilerErrorFactory.NotImplemented(node, binding.ToString()));
					break;
				}
			}
		}
		
		void LoadAddress(Expression expression, ITypeBinding type)
		{
			IBinding binding = GetBinding(expression);
			switch (binding.BindingType)
			{
				case BindingType.Local:
				{				
					_il.Emit(OpCodes.Ldloca, ((LocalBinding)binding).LocalBuilder);
					break;
				}
				
				case BindingType.Parameter:
				{
					_il.Emit(OpCodes.Ldarga, ((ParameterBinding)binding).Index);
					break;
				}
				
				default:
				{
					throw CompilerErrorFactory.NotImplemented(expression, "property access for value types");
					break;
				}
			}
		}
		
		public override void OnSelfLiteralExpression(SelfLiteralExpression node)
		{
			_il.Emit(OpCodes.Ldarg_0);
			PushType(GetBoundType(node));
		}
		
		public override void OnNullLiteralExpression(NullLiteralExpression node)
		{
			_il.Emit(OpCodes.Ldnull);
			PushType(null);
		}
		
		public override void OnReferenceExpression(ReferenceExpression node)
		{	
			IBinding info = BindingManager.GetBinding(node);
			switch (info.BindingType)
			{
				case BindingType.Local:
				{
					LocalBinding local = (LocalBinding)info;
					LocalBuilder builder = local.LocalBuilder;
					_il.Emit(OpCodes.Ldloc, builder);
					PushType(local.BoundType);
					break;
				}
				
				case BindingType.Parameter:
				{
					Bindings.ParameterBinding param = (Bindings.ParameterBinding)info;
					_il.Emit(OpCodes.Ldarg, param.Index);
					PushType(param.BoundType);
					break;
				}
				
				case BindingType.TypeReference:
				{
					EmitGetTypeFromHandle(GetType(node));
					break;
				}
				
				default:
				{
					Errors.Add(CompilerErrorFactory.NotImplemented(node, info.ToString()));
					break;
				}
				
			}			
		}
		
		void SetLocal(BinaryExpression node, LocalBinding binding, bool leaveValueOnStack)
		{
			node.Right.Switch(this); // leaves type on stack
					
			ITypeBinding typeOnStack = null;
			
			if (leaveValueOnStack)
			{	
				typeOnStack = PeekTypeOnStack();
				_il.Emit(OpCodes.Dup);
			}
			else
			{
				typeOnStack = PopType();
			}
			
			// todo: assignment result must be type on the left in the
			// case of casting
			LocalBuilder local = binding.LocalBuilder;
			EmitCastIfNeeded(binding.BoundType, typeOnStack);
			_il.Emit(OpCodes.Stloc, local);
		}
		
		void SetField(Node sourceNode, IFieldBinding field, Expression reference, Expression value, bool leaveValueOnStack)
		{
			OpCode opSetField = OpCodes.Stsfld;
			
			FieldInfo fi = GetFieldInfo(field);			
			if (null != reference)
			{
				if (!field.IsStatic)
				{
					opSetField = OpCodes.Stfld;
					((MemberReferenceExpression)reference).Target.Switch(this);
					PopType();
				}
			}
			
			value.Switch(this);
			EmitCastIfNeeded(field.BoundType, PopType());
			
			LocalBuilder local = null;
			if (leaveValueOnStack)
			{
				_il.Emit(OpCodes.Dup);
				local = _il.DeclareLocal(fi.FieldType);
				_il.Emit(OpCodes.Stloc, local);
			}
			
			_il.Emit(opSetField, fi);
			
			if (leaveValueOnStack)
			{
				_il.Emit(OpCodes.Ldloc, local);
				PushType(field.BoundType);
			}
		}
		
		void SetProperty(Node sourceNode, IPropertyBinding property, Expression reference, Expression value, bool leaveValueOnStack)
		{
			PropertyInfo pi = GetPropertyInfo(property);			
			MethodInfo setMethod = pi.GetSetMethod(true);
			
			if (null != reference)
			{
				if (!setMethod.IsStatic)
				{
					((MemberReferenceExpression)reference).Target.Switch(this);
					PopType();
				}
			}
			
			value.Switch(this);
			EmitCastIfNeeded(property.BoundType, PopType());
			
			LocalBuilder local = null;
			if (leaveValueOnStack)
			{
				_il.Emit(OpCodes.Dup);
				local = _il.DeclareLocal(pi.PropertyType);
				_il.Emit(OpCodes.Stloc, local);
			}
			
			_il.EmitCall(OpCodes.Callvirt, setMethod, null);
			
			if (leaveValueOnStack)
			{
				_il.Emit(OpCodes.Ldloc, local);
				PushType(property.BoundType);
			}
		}
		
		void AddDelegate(Node sourceNode, IBinding eventBinding, Expression value)
		{
			MethodBase mi = GetMethodInfo((IMethodBinding)GetBinding(value));
			if (mi.IsStatic)
			{
				_il.Emit(OpCodes.Ldnull);
			}
			else
			{
				Switch(((MemberReferenceExpression)value).Target); PopType();
			}
			_il.Emit(OpCodes.Ldftn, (MethodInfo)mi);
			
			EventInfo ei = ((ExternalEventBinding)eventBinding).EventInfo;
			
			_il.Emit(OpCodes.Newobj, GetDelegateConstructor(ei.EventHandlerType));					
			_il.EmitCall(OpCodes.Callvirt, ei.GetAddMethod(true), null);
		}
		
		void InitializeMember(Node sourceNode, IBinding binding, Expression value)
		{
			switch (binding.BindingType)
			{
				case BindingType.Property:
				{
					IPropertyBinding property = (IPropertyBinding)binding;
					SetProperty(sourceNode, property, null, value, false);					
					break;
				}
				
				case BindingType.Event:
				{
					AddDelegate(sourceNode, binding, value);
					break;
				}
					
				case BindingType.Field:
				{
					SetField(sourceNode, (IFieldBinding)binding, null, value, false);
					break;					
				}
				
				default:
				{
					throw new ArgumentException("binding");
				}				
			}
		}			
		
		ConstructorInfo GetDelegateConstructor(Type delegateType)
		{
			return delegateType.GetConstructor(DelegateConstructorTypes);
		}
		
		void EmitDebugInfo(Node node)
		{	
			EmitDebugInfo(node, node);
		}
		
		void EmitDebugInfo(Node startNode, Node endNode)
		{
			/*
			LexicalInfo start = startNode.LexicalInfo;
			LexicalInfo end = endNode.LexicalInfo;
			if (start != LexicalInfo.Empty && end != LexicalInfo.Empty)
			{
				_il.MarkSequencePoint(_symbolDocWriter, start.Line, start.StartColumn, end.Line, end.EndColumn);
			}
			*/
		}
		
		void EmitEnumerableBasedFor(ForStatement node, ITypeBinding iteratorType)
		{			
			Label labelTest = _il.DefineLabel();
			Label labelEnd = _il.DefineLabel();
			
			LocalBuilder localIterator = _il.DeclareLocal(Types.IEnumerator);
			EmitGetEnumerableIfNeeded(iteratorType);			
			_il.EmitCall(OpCodes.Callvirt, IEnumerable_GetEnumerator, null);
			_il.Emit(OpCodes.Stloc, localIterator);
			
			// iterator.MoveNext()			
			_il.MarkLabel(labelTest);
			_il.Emit(OpCodes.Ldloc, localIterator);
			_il.EmitCall(OpCodes.Callvirt, IEnumerator_MoveNext, null);
			_il.Emit(OpCodes.Brfalse, labelEnd);			
			
			_il.Emit(OpCodes.Ldloc, localIterator);
			_il.EmitCall(OpCodes.Callvirt, IEnumerator_get_Current, null);
			EmitUnpackForDeclarations(node.Declarations, BindingManager.ObjectTypeBinding);
			
			Switch(node.Block);
			_il.Emit(OpCodes.Br, labelTest);
			
			_il.MarkLabel(labelEnd);			
		}
		
		void EmitArrayBasedFor(ForStatement node, ITypeBinding iteratorTypeBinding)
		{				
			Label labelTest = _il.DefineLabel();
			Label labelEnd = _il.DefineLabel();
			
			Type iteratorType = GetType(iteratorTypeBinding);
			LocalBuilder localIterator = _il.DeclareLocal(iteratorType);
			_il.Emit(OpCodes.Stloc, localIterator);
			
			// i = 0;
			LocalBuilder localIndex = _il.DeclareLocal(Types.Int);
			_il.Emit(OpCodes.Ldc_I4_0);
			_il.Emit(OpCodes.Stloc, localIndex);			
			
			// i<iterator.Length			
			_il.MarkLabel(labelTest);			
			_il.Emit(OpCodes.Ldloc, localIndex);
			_il.Emit(OpCodes.Ldloc, localIterator);
			_il.Emit(OpCodes.Ldlen);
			_il.Emit(OpCodes.Bge, labelEnd);		
			
			// value = iterator[i]
			_il.Emit(OpCodes.Ldloc, localIterator);
			_il.Emit(OpCodes.Ldloc, localIndex);
			_il.Emit(OpCodes.Ldelem_Ref);
			
			EmitUnpackForDeclarations(node.Declarations, iteratorTypeBinding.GetElementType());
			
			Switch(node.Block);
			
			// ++i
			_il.Emit(OpCodes.Ldc_I4_1);
			_il.Emit(OpCodes.Ldloc, localIndex);
			_il.Emit(OpCodes.Add);
			_il.Emit(OpCodes.Stloc, localIndex);
			_il.Emit(OpCodes.Br, labelTest);
			
			_il.MarkLabel(labelEnd);
		}
		
		void EmitUnpackForDeclarations(DeclarationCollection decls, ITypeBinding topOfStack)
		{
			if (1 == decls.Count)
			{
				// for arg in iterator				
				StoreLocal(topOfStack, GetLocalBinding(decls[0]));
			}
			else
			{
				if (topOfStack.IsArray)
				{						
					ITypeBinding elementTypeBinding = topOfStack.GetElementType();
					
					// RuntimeServices.CheckArrayUnpack(array, decls.Count);					
					_il.Emit(OpCodes.Dup);
					_il.Emit(OpCodes.Ldc_I4, decls.Count);					
					_il.EmitCall(OpCodes.Call, RuntimeServices_CheckArrayUnpack, null);
					
					for (int i=0; i<decls.Count; ++i)
					{
						// local = array[i]
						_il.Emit(OpCodes.Dup);
						_il.Emit(OpCodes.Ldc_I4, i); // element index			
						_il.Emit(OpCodes.Ldelem_Ref);
						
						StoreLocal(elementTypeBinding, GetLocalBinding(decls[i]));					
					}
				}
				else
				{
					EmitGetEnumerableIfNeeded(topOfStack);
					_il.EmitCall(OpCodes.Callvirt, IEnumerable_GetEnumerator, null);
					
					foreach (Declaration d in decls)
					{
						_il.Emit(OpCodes.Dup);
						_il.EmitCall(OpCodes.Call, RuntimeServices_MoveNext, null);				
						StoreLocal(BindingManager.ObjectTypeBinding, GetLocalBinding(d));				
					}					
				}
				_il.Emit(OpCodes.Pop);
			}
		}
		
		void EmitGetEnumerableIfNeeded(ITypeBinding topOfStack)
		{
			if (!IsIEnumerableCompatible(topOfStack))
			{
				_il.EmitCall(OpCodes.Call, RuntimeServices_GetEnumerable, null);
			}
		}
		
		bool IsIEnumerableCompatible(ITypeBinding type)
		{
			return BindingManager.IEnumerableTypeBinding.IsAssignableFrom(type);
		}
		
		void PushArguments(IMethodBinding binding, ExpressionCollection args)
		{
			for (int i=0; i<args.Count; ++i)
			{
				Expression arg = args[i];
				arg.Switch(this);
				EmitCastIfNeeded(binding.GetParameterType(i), PopType());
			}
		}
		
		void EmitObjectArray(ExpressionCollection items)
		{
			EmitArray(BindingManager.ObjectTypeBinding, items);
		}
		
		void EmitArray(ITypeBinding type, ExpressionCollection items)
		{
			_il.Emit(OpCodes.Ldc_I4, items.Count);
			_il.Emit(OpCodes.Newarr, GetType(type));
			
			for (int i=0; i<items.Count; ++i)
			{			
				StoreElementReference(i, items[i], type);				
			}
		}
		
		void EmitCastIfNeeded(ITypeBinding expectedType, ITypeBinding actualType)
		{			
			if (null == actualType) // see NullLiteralExpression
			{
				return;
			}
			
			if (!expectedType.IsAssignableFrom(actualType))
			{
				if (expectedType.IsValueType)
				{
					if (actualType.IsValueType)
					{
						// numeric promotion
						_il.Emit(GetNumericPromotionOpCode(expectedType));
					}
					else
					{
						Type type = GetType(expectedType);
						_il.Emit(OpCodes.Unbox, type);
						_il.Emit(OpCodes.Ldobj, type);
					}
				}
				else
				{
					_context.TraceInfo("castclass: expected type='{0}', type on stack='{1}'", expectedType, actualType);
					_il.Emit(OpCodes.Castclass, GetType(expectedType));
				}
			}
			else
			{
				if (expectedType == BindingManager.ObjectTypeBinding)
				{
					if (actualType.IsValueType)
					{
						_il.Emit(OpCodes.Box, GetType(actualType));
					}
				}
			}
		}
		
		OpCode GetNumericPromotionOpCode(ITypeBinding type)
		{
			if (type == BindingManager.IntTypeBinding)
			{
				return OpCodes.Conv_I4;
			}
			else if (type == BindingManager.SingleTypeBinding)
			{
				return OpCodes.Conv_R4;
			}
			else
			{
				throw new NotImplementedException(string.Format("Numeric promotion for {0} not implemented!", type));
			}
		}
		
		void StoreLocal(ITypeBinding topOfStack, LocalBinding local)
		{
			EmitCastIfNeeded(local.BoundType, topOfStack);
			_il.Emit(OpCodes.Stloc, local.LocalBuilder);
		}
		
		void StoreElementReference(int index, Node value, ITypeBinding elementType)
		{
			_il.Emit(OpCodes.Dup);	// array reference
			_il.Emit(OpCodes.Ldc_I4, index); // element index
			value.Switch(this); // value
			EmitCastIfNeeded(elementType, PopType());
			_il.Emit(OpCodes.Stelem_Ref);
		}		
		
		void DefineEntryPoint()
		{
			if (CompilerOutputType.Library != CompilerParameters.OutputType)
			{				
				Method method = AstNormalizationStep.GetEntryPoint(CompileUnit);
				if (null != method)
				{
					Type type = _asmBuilder.GetType(method.DeclaringType.FullName, true);
					MethodInfo createdMethod = type.GetMethod(method.Name, BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
					MethodInfo methodBuilder = GetMethodInfo((IMethodBinding)GetBinding(method));
					
					// the mono implementation expects the first argument to 
					// SetEntryPoint to be a MethodBuilder, otherwise it generates
					// bogus assemblies
					_asmBuilder.SetEntryPoint(methodBuilder, (PEFileKinds)CompilerParameters.OutputType);
					
					// for the rest of the world (like RunAssemblyStep)
					// the created method is the way to go
					CompileUnit[EntryPointKey] = createdMethod;
				}
				else
				{
					Errors.Add(CompilerErrorFactory.NoEntryPoint());
				}
			}
		}	
		
		Type[] GetParameterTypes(Method method)
		{
			ParameterDeclarationCollection parameters = method.Parameters;
			Type[] types = new Type[parameters.Count];
			for (int i=0; i<types.Length; ++i)
			{
				types[i] = GetType(parameters[i].Type);
			}
			return types;
		}
		
		static object EmitInfoKey = new object();
		
		void SetType(TypeDefinition typeDef, Type type)
		{
			typeDef[EmitInfoKey] = type;
		}
		
		void SetBuilder(Node node, object builder)
		{
			node[EmitInfoKey] = builder;
		}
		
		TypeBuilder GetTypeBuilder(Node node)
		{
			return (TypeBuilder)node[EmitInfoKey];
		}
		
		PropertyBuilder GetPropertyBuilder(Node node)
		{
			return (PropertyBuilder)node[EmitInfoKey];
		}
		
		FieldBuilder GetFieldBuilder(Node node)
		{
			return (FieldBuilder)node[EmitInfoKey];
		}
		
		MethodBuilder GetMethodBuilder(Method method)
		{
			return (MethodBuilder)method[EmitInfoKey];
		}
		
		ConstructorBuilder GetConstructorBuilder(Method method)
		{
			return (ConstructorBuilder)method[EmitInfoKey];
		}
		
		LocalBuilder GetLocalBuilder(Node local)
		{
			return GetLocalBinding(local).LocalBuilder;
		}
		
		PropertyInfo GetPropertyInfo(IBinding binding)
		{
			ExternalPropertyBinding external = binding as ExternalPropertyBinding;
			if (null != external)
			{
				return external.PropertyInfo;
			}
			return GetPropertyBuilder(((InternalPropertyBinding)binding).Property);
		}
		
		FieldInfo GetFieldInfo(IFieldBinding binding)
		{
			ExternalFieldBinding external = binding as ExternalFieldBinding;
			if (null != external)
			{
				return external.FieldInfo;
			}
			return GetFieldBuilder(((InternalFieldBinding)binding).Field);
		}
		
		MethodInfo GetMethodInfo(IMethodBinding binding)
		{
			ExternalMethodBinding external = binding as ExternalMethodBinding;
			if (null != external)
			{
				return (MethodInfo)external.MethodInfo;
			}
			return GetMethodBuilder(((InternalMethodBinding)binding).Method);
		}	
		
		ConstructorInfo GetConstructorInfo(IConstructorBinding binding)
		{
			ExternalConstructorBinding external = binding as ExternalConstructorBinding;
			if (null != external)
			{
				return external.ConstructorInfo;
			}
			return GetConstructorBuilder(((InternalMethodBinding)binding).Method);
		}
		
		ITypeBinding ToTypeBinding(Type type)
		{
			return BindingManager.ToTypeBinding(type);
		}
		
		Type GetType(ITypeBinding binding)
		{
			Type type = (Type)_typeCache[binding];
			if (null == type)
			{
				ExternalTypeBinding external = binding as ExternalTypeBinding;
				if (null != external)
				{
					type = external.Type;
				}
				else
				{
					if (binding.IsArray)
					{
						// todo: find a way to create the proper types here
						//string typeName = GetType(binding.GetElementType()) + "[]";
						//type = Type.GetType(typeName, true);
						type = Array.CreateInstance(GetType(binding.GetElementType()), 0).GetType();
					}
					else
					{
						type = GetTypeBuilder(((InternalTypeBinding)binding).TypeDefinition);
					}
				}
				_typeCache.Add(binding, type);
			}
			return type;
		}
		
		Type GetType(Node node)
		{
			return GetType(GetBoundType(node));
		}
		
		TypeAttributes GetTypeAttributes(TypeDefinition type)
		{
			TypeAttributes attributes = TypeAttributes.AnsiClass | TypeAttributes.AutoLayout;
			if (type.IsPublic)
			{
				attributes |= TypeAttributes.Public;
			}
			else
			{
				attributes |= TypeAttributes.NotPublic;
			}
			
			switch (type.NodeType)
			{
				case NodeType.ClassDefinition:
				{
					attributes |= TypeAttributes.Class;
					attributes |= TypeAttributes.Serializable;
					break;
				}
				
				case NodeType.InterfaceDefinition:
				{
					attributes |= TypeAttributes.Interface;
					break;
				}
				
				case NodeType.Module:
				{
					attributes |= TypeAttributes.Sealed;
					break;
				}
			}
			return attributes;
		}
		
		PropertyAttributes GetPropertyAttributes(Property property)
		{
			return PropertyAttributes.None;
		}
		
		MethodAttributes GetMethodAttributes(Method method)
		{
			MethodAttributes attributes = MethodAttributes.HideBySig;
			if (method.IsPublic)
			{
				attributes |= MethodAttributes.Public;			
			}
			else if (method.IsProtected)
			{
				attributes |= MethodAttributes.Family;
			}
			else if (method.IsPrivate)
			{
				attributes |= MethodAttributes.Private;
			}
			
			if (method.IsStatic)
			{
				attributes |= MethodAttributes.Static;
			}
			else if (method.IsOverride)
			{
				attributes |= MethodAttributes.Virtual;
			}
			return attributes;
		}
		
		FieldAttributes GetFieldAttributes(Field field)
		{
			FieldAttributes attributes = 0;
			if (field.IsProtected)
			{
				attributes |= FieldAttributes.Family;
			}
			else if (field.IsPublic)
			{
				attributes |= FieldAttributes.Public;
			}
			else if (field.IsPrivate)
			{
				attributes |= FieldAttributes.Private;
			}
			else if (field.IsInternal)
			{
				attributes |= FieldAttributes.Assembly;			
			}
			if (field.IsStatic)
			{
				attributes |= FieldAttributes.Static;
			}
			return attributes;
		}
		
		void DefineProperty(TypeBuilder typeBuilder, Property property)
		{
			PropertyBuilder builder = typeBuilder.DefineProperty(property.Name, 
			                                            GetPropertyAttributes(property),
			                                            GetType(property.Type),
			                                            new Type[0]);
			Method getter = property.Getter;
			Method setter = property.Setter;
			
			if (null != getter)
			{
				MethodBuilder getterBuilder = DefineMethod(typeBuilder, getter, MethodAttributes.SpecialName);
				builder.SetGetMethod(getterBuilder);
			}
			if (null != setter)
			{
				MethodBuilder setterBuilder = DefineMethod(typeBuilder, setter, MethodAttributes.SpecialName);
				builder.SetSetMethod(setterBuilder);
			}
			
			foreach (Boo.Lang.Ast.Attribute attribute in property.Attributes)
			{
				builder.SetCustomAttribute(GetCustomAttributeBuilder(attribute));
			}
			
			SetBuilder(property, builder);
		}
		
		void DefineField(TypeBuilder typeBuilder, Field field)
		{
			FieldBuilder builder = typeBuilder.DefineField(field.Name, 
			                                               GetType(field), 
			                                               GetFieldAttributes(field));
			SetBuilder(field, builder);
		}
		
		MethodBuilder DefineMethod(TypeBuilder typeBuilder, Method method, MethodAttributes attributes)
		{			
			MethodBuilder builder = typeBuilder.DefineMethod(method.Name, 
                                        GetMethodAttributes(method) | attributes,
                                        GetType(method.ReturnType),
                                        GetParameterTypes(method));
			/*
			InternalMethodBinding binding = (InternalMethodBinding)GetBinding(method);
			IMethodBinding overriden = binding.Override;
			if (null != overriden)
			{
				typeBuilder.DefineMethodOverride(builder, GetMethodInfo(overriden));
			}
			*/
			SetBuilder(method, builder);
			foreach (Boo.Lang.Ast.Attribute attribute in method.Attributes)
			{
				builder.SetCustomAttribute(GetCustomAttributeBuilder(attribute));
			}			
			return builder;
		}
		
		void DefineConstructor(TypeBuilder typeBuilder, Method constructor)
		{
			ConstructorBuilder builder = typeBuilder.DefineConstructor(GetMethodAttributes(constructor),
			                               CallingConventions.Standard, 
			                               GetParameterTypes(constructor));
			SetBuilder(constructor, builder);
		}
		
		void DefineType(TypeDefinition typeDefinition)
		{
			TypeBuilder typeBuilder = _moduleBuilder.DefineType(typeDefinition.FullName,
										GetTypeAttributes(typeDefinition));			
			SetType(typeDefinition, typeBuilder);
		}
		
		void EmitBaseTypesAndAttributes(TypeDefinition typeDefinition, TypeBuilder typeBuilder)
		{
			foreach (TypeReference baseType in typeDefinition.BaseTypes)
			{
				Type type = GetType(baseType);
				if (type.IsClass)
				{
					typeBuilder.SetParent(type);
				}
				else
				{
					typeBuilder.AddInterfaceImplementation(type);
				}
			}
			
			foreach (Boo.Lang.Ast.Attribute attribute in typeDefinition.Attributes)
			{
				typeBuilder.SetCustomAttribute(GetCustomAttributeBuilder(attribute));
			}
		}
		
		CustomAttributeBuilder GetCustomAttributeBuilder(Boo.Lang.Ast.Attribute node)
		{
			IConstructorBinding constructor = (IConstructorBinding)GetBinding(node);
			ConstructorInfo constructorInfo = GetConstructorInfo(constructor);
			object[] constructorArgs = GetValues(node.Arguments);
			
			ExpressionPairCollection namedArgs = node.NamedArguments;
			if (namedArgs.Count > 0)
			{
				PropertyInfo[] namedProperties;
				object[] propertyValues;
				FieldInfo[] namedFields;
				object[] fieldValues;
				GetNamedValues(namedArgs,
								out namedProperties, out propertyValues,
								out namedFields, out fieldValues);
				return new CustomAttributeBuilder(
								constructorInfo, constructorArgs,
								namedProperties, propertyValues,
								namedFields, fieldValues);
			}			
			return new CustomAttributeBuilder(constructorInfo, constructorArgs);
		}
		
		void GetNamedValues(ExpressionPairCollection values, 
							out PropertyInfo[] outNamedProperties,
							out object[] outPropertyValues,
							out FieldInfo[] outNamedFields,
							out object[] outFieldValues)
		{
			ArrayList namedProperties = new ArrayList();
			ArrayList propertyValues = new ArrayList();
			ArrayList namedFields = new ArrayList();
			ArrayList fieldValues = new ArrayList();
			foreach (ExpressionPair pair in values)
			{
				IBinding binding = GetBinding(pair.First);
				if (BindingType.Property == binding.BindingType)
				{
					namedProperties.Add(GetPropertyInfo(binding));
					propertyValues.Add(GetValue(pair.Second));
				}
				else
				{
					namedFields.Add(GetFieldInfo((IFieldBinding)binding));
					fieldValues.Add(GetValue(pair.Second));
				}
			}
			
			outNamedProperties = (PropertyInfo[])namedProperties.ToArray(typeof(PropertyInfo));
			outPropertyValues = (object[])propertyValues.ToArray();
			outNamedFields = (FieldInfo[])namedFields.ToArray(typeof(FieldInfo));
			outFieldValues = (object[])fieldValues.ToArray();
		}
		
		object[] GetValues(ExpressionCollection expressions)
		{
			object[] values = new object[expressions.Count];
			for (int i=0; i<values.Length; ++i)
			{
				values[i] = GetValue(expressions[i]);
			}
			return values;
		}
		
		object GetValue(Expression expression)
		{
			switch (expression.NodeType)
			{
				case NodeType.StringLiteralExpression:
				{
					return ((StringLiteralExpression)expression).Value;
				}
				
				case NodeType.BoolLiteralExpression:
				{
					return ((BoolLiteralExpression)expression).Value;
				}
				
				default:
				{
					IBinding binding = GetBinding(expression);
					if (BindingType.TypeReference == binding.BindingType)
					{
						return GetType(expression);
					}
					break;
				}
			}
			Errors.Add(CompilerErrorFactory.NotImplemented(expression, "Expression value"));
			return null;
		}
		
		void DefineTypeMembers(TypeDefinition typeDefinition)
		{
			TypeBuilder typeBuilder = GetTypeBuilder(typeDefinition);
			TypeMemberCollection members = typeDefinition.Members;
			foreach (TypeMember member in members)
			{
				switch (member.NodeType)
				{
					case NodeType.Method:
					{
						DefineMethod(typeBuilder, (Method)member, 0);
						break;
					}
					
					case NodeType.Constructor:
					{
						DefineConstructor(typeBuilder, (Constructor)member);
						break;
					}
					
					case NodeType.Field:
					{
						DefineField(typeBuilder, (Field)member);
						break;
					}
					
					case NodeType.Property:
					{
						DefineProperty(typeBuilder, (Property)member);
						break;
					}
				}
			}
		}
		
		string GetAssemblyName(string fname)
		{
			return Path.GetFileNameWithoutExtension(fname);
		}
		
		string GetTargetDirectory(string fname)
		{
			return Path.GetDirectoryName(Path.GetFullPath(fname));
		}
		
		string BuildOutputAssemblyName()
		{			
			CompilerParameters parameters = CompilerParameters;
			string fname = parameters.OutputAssembly;
			if (!Path.HasExtension(fname))
			{
				if (CompilerOutputType.Library == parameters.OutputType)
				{
					fname += ".dll";
				}
				else
				{
					fname += ".exe";
			
				}
			}
			return Path.GetFullPath(fname);
		}
		
		void SetUpAssembly()
		{
			if (0 == CompilerParameters.OutputAssembly.Length)
			{				
				CompilerParameters.OutputAssembly = CompileUnit.Modules[0].Name;			
			}
			
			CompilerParameters.OutputAssembly = BuildOutputAssemblyName();
			
			AssemblyName asmName = new AssemblyName();
			asmName.Name = GetAssemblyName(CompilerParameters.OutputAssembly);
			
			_asmBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndSave, GetTargetDirectory(CompilerParameters.OutputAssembly));
			_moduleBuilder = _asmBuilder.DefineDynamicModule(asmName.Name, Path.GetFileName(CompilerParameters.OutputAssembly), true);			
			CompileUnit[AssemblyBuilderKey] = _asmBuilder;
			CompileUnit[ModuleBuilderKey] = _moduleBuilder;
		}
	}
}
