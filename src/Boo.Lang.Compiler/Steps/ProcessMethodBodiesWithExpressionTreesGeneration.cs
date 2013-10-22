using Boo.Lang.Compiler.Ast;
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
            if (!GeneratorExpressionTrees.ShouldConvertToLinqExpression(node)) return false;
            var result = GeneratorExpressionTrees.Linqify(node);
            Visit(result);
            MarkVisited(node);
            MarkVisited(result);
            node.ParentNode.Replace(node, result);
            return true;
        }
    }
}