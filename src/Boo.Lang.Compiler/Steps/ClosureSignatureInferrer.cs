﻿#region license
// Copyright (c) 2003, 2004, 2005 Rodrigo B. de Oliveira (rbo@acm.org)
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
// 
//     * Redistributions of source code must retain the above copyright notice,
//     this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice,
//     this list of conditions and the following disclaimer in the documentation
//     and/or other materials provided with the distribution.
//     * Neither the name of Rodrigo B. de Oliveira nor the names of its
//     contributors may be used to endorse or promote products derived from this
//     software without specific prior written permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
// THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#endregion


using System;
using System.Collections.Generic;
using System.Text;

using Boo.Lang.Compiler.Ast;
using Boo.Lang.Compiler.TypeSystem;
using Boo.Lang.Environments;
using Boo.Lang.Compiler.TypeSystem.Generics;

namespace Boo.Lang.Compiler.Steps
{
	class ClosureSignatureInferrer
	{
		private BlockExpression _closure;
		private IType[] _inputTypes;

		public ClosureSignatureInferrer(BlockExpression closure)
		{
			_closure = closure;
			InitializeInputTypes();
		}

		public BlockExpression Closure
		{
			get { return _closure; }
		}

		public IType[] ParameterTypes
		{
			get { return _inputTypes; }
		}

		protected BooCodeBuilder CodeBuilder
		{
			get { return My<BooCodeBuilder>.Instance; }
		}

		public MethodInvocationExpression MethodInvocationContext
		{
			get 
			{
				MethodInvocationExpression mie = Closure.ParentNode as MethodInvocationExpression;
				if (mie != null && mie.Arguments.Contains(Closure)) return mie;
				return null;
			}
		}

		private void InitializeInputTypes()
		{
			_inputTypes = Array.ConvertAll<ParameterDeclaration, IType>(
				Closure.Parameters.ToArray(),
				delegate(ParameterDeclaration pd) { return pd.Type == null ? null : pd.Type.Entity as IType; });
		}

		public ICallableType InferCallableType()
		{
			ICallableType contextType = (
				GetTypeFromMethodInvocationContext() ??
				GetTypeFromDeclarationContext() ??
				GetTypeFromBinaryExpressionContext() ??
				GetTypeFromCastContext() ??
				GetTypeFromMethodContext()) as ICallableType;

			return contextType;
		}

		private IType GetTypeFromMethodContext()
		{
			Node parent = Closure.ParentNode;
			while (parent != null)
			{
				if (parent is Method)
				{
					break;
				}
				parent = parent.ParentNode;
			};
			if (parent != null)
			{
				var method = (Method)parent;
                if (method.ReturnType != null)
                {
                    var type = TypeSystemServices.GetType(method.ReturnType);
                    return CheckForExpression(type);
                }
			}
			return null;
		}

		private IType CheckForExpression(IType type)
		{
			if (GenericParameterInferrer.IsOfLinqExpressionType(type))
			{
				IsExpression = true;
				type = GenericParameterInferrer.GetCallableTypeFromExpressionType(type);
			}
			return type;
		}

		private IType GetTypeFromBinaryExpressionContext()
		{
			BinaryExpression binary = Closure.ParentNode as BinaryExpression;
			if (binary == null || Closure != binary.Right) return null;
			return CheckForExpression(binary.Left.ExpressionType);
		}

		private IType GetTypeFromDeclarationContext()
		{
			TypeReference tr = null;
			DeclarationStatement ds = Closure.ParentNode as DeclarationStatement;
			if (ds != null)
			{
				tr = ds.Declaration.Type;
			}
			
			Field fd = Closure.ParentNode as Field;
			if (fd != null)
			{
				tr = fd.Type;
			}

			if (tr != null) return CheckForExpression(tr.Entity as IType);
			return null;
		}

        private IType GetTypeFromMethodInvocationContext()
        {
            if (MethodInvocationContext == null) return null;

            int argumentIndex = MethodInvocationContext.Arguments.IndexOf(Closure);

            IMethod method = MethodInvocationContext.Target.Entity as IMethod;
            ILocalEntity local = MethodInvocationContext.Target.Entity as ILocalEntity;
            IParameter[] parameters = null;
            if (method != null)
            {
                parameters = method.GetParameters();
            }
            else if (local != null)
            {
                var callableType = local.Type as ICallableType;
                if (callableType != null)
                {
                    parameters = callableType.GetSignature().Parameters;
                }
            }
            if (parameters == null) return null;

            if (argumentIndex < parameters.Length) return CheckForExpression(parameters[argumentIndex].Type);
            if (method.AcceptVarArgs) return CheckForExpression(parameters[parameters.Length - 1].Type);

            return null;
        }

		private IType GetTypeFromCastContext()
		{
			TryCastExpression tryCast = Closure.ParentNode as TryCastExpression;
			if (tryCast != null) return CheckForExpression(tryCast.Type.Entity as IType);

			CastExpression cast = Closure.ParentNode as CastExpression;
			if (cast != null) return CheckForExpression(cast.Type.Entity as IType);

			return null;
		}

		private void InferInputTypesFromContextType(ICallableType type)
		{
			CallableSignature sig = type.GetSignature();
			for (int i = 0; i < Math.Min(ParameterTypes.Length, sig.Parameters.Length); i++)
			{
				if (ParameterTypes[i] != null) continue;
				ParameterTypes[i] = sig.Parameters[i].Type;
			}
		}

		public bool HasUntypedInputParameters()
		{
			return Array.IndexOf(ParameterTypes, null) != -1;
		}

		public bool IsExpression { get; set; }
	}
}
