using Boo.Lang.Compiler.Ast;
using Boo.Lang.Compiler.TypeSystem;
using Boo.Lang.Compiler.TypeSystem.Generics;
using Boo.Lang.Compiler.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Boo.Lang.Compiler.Steps
{
    public class ProcessMethodBodiesWithExpressionTreesGeneration : ProcessMethodBodiesWithDuckTyping
    {
        public override void OnBlockExpression(Ast.BlockExpression node)
        {
            if (WasVisited(node)) return;
            if (ConvertToExpressionTree(node)) return;
            base.OnBlockExpression(node);
        }

        private bool ConvertToExpressionTree(BlockExpression node)
        {
            if (!ShouldConvertToLinqExpression(node)) return false;
            var result = Linqify(node);
            Visit(result);
            MarkVisited(node);
            MarkVisited(result);
            node.ParentNode.Replace(node, result);
            return true;
        }

        public Expression Linqify(BlockExpression expr)
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

            var p1 = expr.Parameters[0];
            p1.Type = p1.Type ?? GetLambdaParameterType(expr);

            //to get return type of the expression we clone original expression and run it through the pipeline
            //normally the method for the closure will be added but we remove it as it is not needed
            TypeMember addedClosure = null;
            EventHandler membersChanged = (sender, e) =>
            {
                addedClosure = CurrentMethod.DeclaringType.Members.Last;
            };
            CurrentMethod.DeclaringType.Members.Changed += membersChanged;
            var clone = expr.CloneNode();
            Visit(clone);
            CurrentMethod.DeclaringType.Members.Changed -= membersChanged;
            CurrentMethod.DeclaringType.Members.Remove(addedClosure); 
            var exprReturnType = CodeBuilder.CreateTypeReference((clone.Body.FirstStatement as ReturnStatement).Expression.ExpressionType);

            var p1Init = new DeclarationStatement(new Declaration(expr.LexicalInfo, CompilerContext.Current.GetUniqueName(p1.Name)),
                new MethodInvocationExpression(ReferenceExpression.Lift("System.Linq.Expressions.Expression.Parameter"),
                    new TypeofExpression() { Type = p1.Type },
                    new StringLiteralExpression(p1.Name)));
            var p1Ref = new ReferenceExpression(p1Init.Declaration.Name);

            var visitor = new GeneratorExpressionTrees(p1.Name, p1Ref, NameResolutionService);
            visitor.Visit(expr.Body);

            var lambdaArg = new GenericTypeReference("System.Func", p1.Type, exprReturnType);
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
            NameResolutionService.ResolveSimpleTypeReference(returnType);
            linqify.ReturnType = returnType;
            linqify.Body.Add(p1Init);
            linqify.Body.Add(new ReturnStatement(resultExpr));
            return new MethodInvocationExpression(linqify);
        }

        private TypeReference GetLambdaParameterType(BlockExpression expr)
        {
            Node parent = expr.ParentNode;
            while (parent != null)
            {
                if (parent is DeclarationStatement)
                {
                    return GetLambdaParameterType(parent as DeclarationStatement);
                }
                else if (parent is Method)
                {
                    return GetLambdaParameterType(parent as Method);
                }
                else if (parent is MethodInvocationExpression)
                {
                    return GetLambdaParameterType(parent as MethodInvocationExpression, expr);
                }
                parent = parent.ParentNode;
            };
            return null;
        }

        private TypeReference GetLambdaParameterType(Method method)
        {
            return GetLambdaParameterType(method.ReturnType);
        }

        private TypeReference GetLambdaParameterType(DeclarationStatement declarationStatement)
        {
            return GetLambdaParameterType(declarationStatement.Declaration.Type);
        }

        private TypeReference GetLambdaParameterType(TypeReference typeReference)
        {
            var type = TypeSystemServices.GetType(typeReference);
            var funcGenericArgument = type.ConstructedInfo.GenericArguments[0];
            return CodeBuilder.CreateTypeReference(funcGenericArgument.ConstructedInfo.GenericArguments[0]);
        }

        private TypeReference GetLambdaParameterType(MethodInvocationExpression methodInvocationExpression, BlockExpression expr)
        {
            var parent = expr.ParentNode as MethodInvocationExpression;
            var targetEntity = TargetEntity(parent, expr) as IMethod;
            var target = EnsureMemberReference(parent).Target;

            var targetType = target.ExpressionType.ConstructedInfo.GenericArguments[0];
            return CodeBuilder.CreateTypeReference(targetType);
        }

        private MemberReferenceExpression EnsureMemberReference(MethodInvocationExpression node)
        {
            Expression target = node.Target;
            GenericReferenceExpression gre = target as GenericReferenceExpression;
            if (null != gre)
                target = gre.Target;

            MemberReferenceExpression memberRef = target as MemberReferenceExpression;
            if (null != memberRef)
                return memberRef;

            node.Target = memberRef = CodeBuilder.MemberReferenceForEntity(
                CreateSelfReference(),
                GetEntity(node.Target));

            return memberRef;
        }

        private SelfLiteralExpression CreateSelfReference()
        {
            return CodeBuilder.CreateSelfReference(CurrentType);
        }

        private IEntity TargetEntity(MethodInvocationExpression parent, BlockExpression expr)
        {
            Ambiguous ambiguous = parent.Target.Entity as Ambiguous;
            IEntity targetEntity;
            if (ambiguous != null)
            {
                targetEntity = ambiguous.Entities.First(e => NeedsLinqExpression(e, parent.Arguments, expr));
            }
            else
            {
                targetEntity = parent.Target.Entity;
            }
            return targetEntity;
        }

        public bool ShouldConvertToLinqExpression(BlockExpression node)
        {
            Node parent = node.ParentNode;
            while (parent != null)
            {
                if (parent is DeclarationStatement)
                {
                    return ReturnsLinqExpression(parent as DeclarationStatement);
                }
                else if (parent is Method)
                {
                    return ReturnsLinqExpression(parent as Method);
                }
                else if (parent is MethodInvocationExpression)
                {
                    return NeedsLinqExpression(parent as MethodInvocationExpression, node);
                }
                parent = parent.ParentNode;
            };
            return false;
        }

        private bool ReturnsLinqExpression(DeclarationStatement statement)
        {
            return IsOfLinqExpressionType(statement.Declaration.Type);
        }

        private bool ReturnsLinqExpression(Method node)
        {
            return IsOfLinqExpressionType(node.ReturnType);
        }

        private bool NeedsLinqExpression(MethodInvocationExpression parent, BlockExpression node)
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

        private bool NeedsLinqExpression(IEntity entity, ExpressionCollection arguments, BlockExpression node)
        {
            var entityWithParams = entity as IEntityWithParameters;
            if (entityWithParams == null) return false;
            var ix = arguments.IndexOf(node);
            var entityParameters = entityWithParams.GetParameters();
            if (arguments.Count > entityParameters.Length)
            {
                return false;
            }
            if (arguments.Count < entityParameters.Length)
            {
                ix += entityParameters.Length - arguments.Count;
            }
            var targetParameter = entityParameters[ix];
            return IsOfLinqExpressionType(targetParameter.Type);
        }

        private bool IsOfLinqExpressionType(TypeReference typeReference)
        {
            return typeReference != null && IsOfLinqExpressionType(TypeSystemServices.GetType(typeReference));
        }

        private bool IsOfLinqExpressionType(IType type)
        {
            return type.FullName == TypeUtilities.GetFullName(typeof(System.Linq.Expressions.Expression<>));
        }
    }
}