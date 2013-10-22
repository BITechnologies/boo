using Boo.Lang.Compiler.Ast;
using Boo.Lang.Compiler.TypeSystem;
using Boo.Lang.Compiler.TypeSystem.Services;
using Boo.Lang.Compiler.Util;
using Boo.Lang.Environments;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        public GeneratorExpressionTrees(string parameter, ReferenceExpression parameterRef, NameResolutionService nameResolutionService)
        {
            _parameter = parameter;
            _parameterRef = parameterRef;
            _nameResolutionService = nameResolutionService;
        }

        public static Expression Linqify(BlockExpression expr)
        {
            //assert on param
            //assert one expression statement
            if (expr.Parameters.Count != 1)
            {
                throw new NotSupportedException("Only lambdas with one parameter are supported");
            }
            if (expr.Body.Statements.Count != 1 &&
                expr.Body.FirstStatement as ExpressionStatement == null)
            {
                throw new NotSupportedException("A lambda expression with a statement body cannot be converted to an expression tree");
            }
            NameResolutionService nameResolutionService = new EnvironmentProvision<NameResolutionService>();

            var p1 = expr.Parameters[0];
            var p1Init = new DeclarationStatement(new Declaration(expr.LexicalInfo, CompilerContext.Current.GetUniqueName(p1.Name)),
                new MethodInvocationExpression(ReferenceExpression.Lift("System.Linq.Expressions.Expression.Parameter"),
                    new TypeofExpression() { Type = p1.Type },
                    new StringLiteralExpression(p1.Name)));
            var p1Ref = new ReferenceExpression(p1Init.Declaration.Name);

            var visitor = new GeneratorExpressionTrees(p1.Name, p1Ref, nameResolutionService);
            visitor.Visit(expr.Body);

            var lambdaArg = new GenericTypeReference("System.Func", p1.Type, TypeReference.Lift(typeof(bool)));
            var lambdaCall = new GenericReferenceExpression()
            {
                Target = ReferenceExpression.Lift("System.Linq.Expressions.Expression.Lambda"),
                GenericArguments = { lambdaArg }
            };
            var resultExpr = new MethodInvocationExpression(lambdaCall,
                visitor.Expression,
                p1Ref);

            var linqify = new BlockExpression();
            var returnType = new GenericTypeReference("System.Linq.Expressions.Expression", lambdaArg);
            nameResolutionService.ResolveSimpleTypeReference(returnType);
            linqify.ReturnType = returnType;
            linqify.Body.Add(p1Init);
            linqify.Body.Add(new ReturnStatement(resultExpr));
            return new MethodInvocationExpression(linqify);
        }

        public static bool ShouldConvertToLinqExpression(BlockExpression node)
        {
            Node parent = node.ParentNode;
            while (parent != null)
            {
                if (parent is DeclarationStatement)
                {
                    return GeneratorExpressionTrees.ReturnsLinqExpression(parent as DeclarationStatement);
                }
                else if (parent is Method)
                {
                    return GeneratorExpressionTrees.ReturnsLinqExpression(parent as Method);
                }
                else if (parent is MethodInvocationExpression)
                {
                    return GeneratorExpressionTrees.NeedsLinqExpression(parent as MethodInvocationExpression, node);
                }
                parent = parent.ParentNode;
            };
            return false;
        }

        private static bool ReturnsLinqExpression(DeclarationStatement statement)
        {
            return IsOfLinqExpressionType(statement.Declaration.Type);
        }

        private static bool ReturnsLinqExpression(Method node)
        {
            return IsOfLinqExpressionType(node.ReturnType);
        }

        private static bool NeedsLinqExpression(MethodInvocationExpression parent, BlockExpression node)
        {
            if (!parent.Arguments.Contains(node))
            {
                return false;
            }
            Ambiguous targetEntity = parent.Target.Entity as Ambiguous;
            if (targetEntity != null)
            {
                return targetEntity.Entities.Any(e => NeedsLinqExpression(e, parent.Arguments, node));
            }

            return NeedsLinqExpression(parent.Target.Entity, parent.Arguments, node);
        }

        private static bool NeedsLinqExpression(IEntity entity, ExpressionCollection arguments, BlockExpression node)
        {
            var entityWithParams = entity as IEntityWithParameters;
            if (entityWithParams == null) return false;
            var ix = arguments.IndexOf(node);
            var entityParameters = entityWithParams.GetParameters();
            if (arguments.Count < entityParameters.Length)
            {
                ix++;
            }
            var targetParameter = entityWithParams.GetParameters()[ix];
            return IsOfLinqExpressionType(targetParameter.Type);
        }

        private static bool IsOfLinqExpressionType(TypeReference typeReference)
        {
            return IsOfLinqExpressionType(TypeSystemServices.GetType(typeReference));
        }

        private static bool IsOfLinqExpressionType(IType type)
        {
            return type.FullName == TypeUtilities.GetFullName(typeof(System.Linq.Expressions.Expression<>));
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

        public override void OnBinaryExpression(BinaryExpression node)
        {
            base.OnBinaryExpression(node);
            var expression = GetLinqExpressionForOperator(node.Operator);
            ReplaceCurrentNode(new MethodInvocationExpression(
                ReferenceExpression.Lift(expression),
                node.Left, node.Right));
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
                ReplaceCurrentNode(new MethodInvocationExpression(
                    ReferenceExpression.Lift("System.Linq.Expressions.Expression.PropertyOrField"),
                    node.Target,
                    new StringLiteralExpression(node.Name)));
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
                ReplaceWithCallToExpressionConstant(node);
            }
        }

        public override void OnMethodInvocationExpression(MethodInvocationExpression node)
        {
            var target = (MemberReferenceExpression)node.Target;
            _methodsStack.Push(new CurrentMethod() { Target = target, Name = target.Name });
            base.OnMethodInvocationExpression(node);
            var currentMethod = _methodsStack.Pop();
            var expressionCallInvocation = new MethodInvocationExpression(
                ReferenceExpression.Lift("System.Linq.Expressions.Expression.Call"),
                node.Target,
                new StringLiteralExpression(currentMethod.Name),
                new NullLiteralExpression());
            expressionCallInvocation.Arguments.AddRange(node.Arguments);
            ReplaceCurrentNode(expressionCallInvocation);
        }

        public override void OnExpressionStatement(ExpressionStatement node)
        {
            base.OnExpressionStatement(node);
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

        public Expression Expression { get; private set; }
    }
}