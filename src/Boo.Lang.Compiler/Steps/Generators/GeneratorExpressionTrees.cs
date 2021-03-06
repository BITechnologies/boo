﻿using Boo.Lang.Compiler.Ast;
using Boo.Lang.Compiler.TypeSystem;
using Boo.Lang.Compiler.TypeSystem.Services;
using System;
using System.Collections.Generic;
using Boo.Lang.Runtime;
using Boo.Lang.Compiler.Util;
using System.Linq;

namespace Boo.Lang.Compiler.Steps
{
    public class GeneratorExpressionTrees : DepthFirstTransformer
    {
        private class CurrentMethod
        {
            public MemberReferenceExpression Target { get; set; }
            public string Name { get; set; }
        };
        private string _parameter;
        private ReferenceExpression _parameterRef;
        private NameResolutionService _nameResolutionService;
        private Stack<CurrentMethod> _methodsStack = new Stack<CurrentMethod>();
        private BooCodeBuilder _codeBuilder;

        public GeneratorExpressionTrees(string parameter, ReferenceExpression parameterRef, NameResolutionService nameResolutionService, BooCodeBuilder codeBuilder)
        {
            _parameter = parameter;
            _parameterRef = parameterRef;
            _nameResolutionService = nameResolutionService;
            _codeBuilder = codeBuilder;
        }

        private string GetLinqExpressionForOperator(BinaryOperatorType binaryOperatorType)
        {
            switch (binaryOperatorType)
            {
                case BinaryOperatorType.Equality:
                case BinaryOperatorType.ReferenceEquality:
                    return "System.Linq.Expressions.Expression.Equal";
                case BinaryOperatorType.ReferenceInequality:
                case BinaryOperatorType.Inequality:
                    return "System.Linq.Expressions.Expression.NotEqual";
                case BinaryOperatorType.LessThan:
                    return "System.Linq.Expressions.Expression.LessThan";
                case BinaryOperatorType.LessThanOrEqual:
                    return "System.Linq.Expressions.Expression.LessThanOrEqual";
                case BinaryOperatorType.GreaterThan:
                    return "System.Linq.Expressions.Expression.GreaterThan";
                case BinaryOperatorType.GreaterThanOrEqual:
                    return "System.Linq.Expressions.Expression.GreaterThanOrEqual";
                case BinaryOperatorType.Or:
                    return "System.Linq.Expressions.Expression.OrElse";
                case BinaryOperatorType.And:
                    return "System.Linq.Expressions.Expression.AndAlso";
                default:
                    throw new NotSupportedException();
            }
        }

        private void ReplaceWithCallToExpressionConstant(Expression node)
        {
            ReplaceCurrentNode(new MethodInvocationExpression(ReferenceExpression.Lift("System.Linq.Expressions.Expression.Constant"), node));
        }

        private void ReplaceWithCallToExpressionConvert(Expression target, TypeReference type)
        {
            ReplaceCurrentNode(new MethodInvocationExpression(ReferenceExpression.Lift("System.Linq.Expressions.Expression.Convert"),
                target,
                new TypeofExpression() { Type = type }));
        }

        public override void OnBinaryExpression(BinaryExpression node)
        {
            var lhs = node.Left.ExpressionType;
            var rhs = node.Right.ExpressionType;
            base.OnBinaryExpression(node);
            var expression = GetLinqExpressionForOperator(node.Operator);
            ReplaceCurrentNode(new MethodInvocationExpression(
                ReferenceExpression.Lift(expression),
                AddOptionalConvert(rhs, lhs, node.Left), 
                AddOptionalConvert(lhs, rhs, node.Right)));
        }

        public override void OnUnaryExpression(UnaryExpression node)
        {
            base.OnUnaryExpression(node);
            if (node.Operator == UnaryOperatorType.LogicalNot)
            {
                ReplaceCurrentNode(new MethodInvocationExpression(
                    ReferenceExpression.Lift("System.Linq.Expressions.Expression.Not"),
                    node.Operand));
            }
        }

        public override void OnMemberReferenceExpression(MemberReferenceExpression node)
        {
            base.OnMemberReferenceExpression(node);
            if (_methodsStack.Count > 0 && node == _methodsStack.Peek().Target)
            {
                ReplaceCurrentNode(node.Target);
            }
            else
            {
                if (node.Target is ReferenceExpression && node.Target != _parameterRef)
                {
                    var target = new TypeofExpression()
                    {
                        Type = _codeBuilder.CreateTypeReference(node.Target.ExpressionType)
                    };
                    ReplaceCurrentNode(new MethodInvocationExpression(
                        ReferenceExpression.Lift("Boo.Lang.Runtime.RuntimeServices.StaticPropertyOrField"),
                        target,
                        new StringLiteralExpression(node.Name)));
                }
                else
                {
                    ReplaceCurrentNode(new MethodInvocationExpression(
                        ReferenceExpression.Lift("System.Linq.Expressions.Expression.PropertyOrField"),
                        node.Target,
                        new StringLiteralExpression(node.Name)));
                }
            }
        }

        public override void OnReferenceExpression(ReferenceExpression node)
        {
            if (_parameter == node.Name)
            {
                ReplaceCurrentNode(_parameterRef);
            }
            else
            {
                if (node.Entity.EntityType != TypeSystem.EntityType.Type)
                {
                    ReplaceWithCallToExpressionConstant(node);
                }
            }
        }

        public override void OnMethodInvocationExpression(MethodInvocationExpression node)
        {
            var target = (MemberReferenceExpression)node.Target;
            _methodsStack.Push(new CurrentMethod() { Target = target, Name = target.Name });
            base.OnMethodInvocationExpression(node);
            var currentMethod = _methodsStack.Pop();
            var ga = (currentMethod.Target.Entity as IMethod).ConstructedInfo.GenericArguments ?? new IType[0];
            ExpressionCollection methodGenericArguments = new ExpressionCollection();
            methodGenericArguments.AddRange(ga.Select(a => new TypeofExpression { Type = _codeBuilder.CreateTypeReference(a) } as Expression));
            Expression exprCallTypeArgs = methodGenericArguments.Count > 0
                ? _codeBuilder.CreateArray(_codeBuilder.TypeSystemServices.Map(typeof(Type[])), methodGenericArguments)
                : new NullLiteralExpression() as Expression;
            Expression exprTarget = node.Target.Entity.EntityType == TypeSystem.EntityType.Type
                ? new TypeofExpression { Type = _codeBuilder.CreateTypeReference(node.Target.Entity as IType) }
                : node.Target;
            var expressionCallInvocation = new MethodInvocationExpression(
                ReferenceExpression.Lift("System.Linq.Expressions.Expression.Call"),
                exprTarget,
                new StringLiteralExpression(currentMethod.Name),
                exprCallTypeArgs);
            expressionCallInvocation.Arguments.AddRange(node.Arguments);
            ReplaceCurrentNode(expressionCallInvocation);
        }

        public override void OnExpressionStatement(ExpressionStatement node)
        {
            base.OnExpressionStatement(node);
            this.Expression = node.Expression;
        }

        public override void OnReturnStatement(ReturnStatement node)
        {
            base.OnReturnStatement(node);
            this.Expression = node.Expression;
        }

        public override void OnStringLiteralExpression(StringLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnNullLiteralExpression(NullLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnIntegerLiteralExpression(IntegerLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnDoubleLiteralExpression(DoubleLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnTimeSpanLiteralExpression(TimeSpanLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnBoolLiteralExpression(BoolLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnListLiteralExpression(ListLiteralExpression node)
        {
            ReplaceWithCallToExpressionConstant(node);
        }

        public override void OnTryCastExpression(TryCastExpression node)
        {
            base.OnTryCastExpression(node);
            ReplaceWithCallToExpressionConvert(node.Target, node.Type);
        }

        public override void OnCastExpression(CastExpression node)
        {
            base.OnCastExpression(node);
            ReplaceWithCallToExpressionConvert(node.Target, node.Type);
        }
        public Expression Expression { get; private set; }


        private Expression AddOptionalConvert(IType to, IType from, Expression expression)
        {
            if (to == null || from == null)
                return expression;

            if (TypeSystemServices.IsNullable(to) && from.IsValueType && !TypeSystemServices.IsNullable(from))
            {
                var convertCall = ReferenceExpression.Lift("System.Linq.Expressions.Expression.Convert");
                expression = new MethodInvocationExpression(convertCall,
                    expression,
                    new TypeofExpression() { Type = _codeBuilder.CreateTypeReference(to) });
            }
            return expression;
        }
    }
}