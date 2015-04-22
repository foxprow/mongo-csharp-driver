﻿/* Copyright 2010-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq.Expressions;
using MongoDB.Driver.Linq.Processors.MethodCallBinders;

namespace MongoDB.Driver.Linq.Processors
{
    internal class ProjectionBinder
    {
        private static IMethodCallBinder __methodCallBinder;

        static ProjectionBinder()
        {
            var nameBasedBinder = new NameBasedMethodCallBinder();

            nameBasedBinder.Register(new AverageBinder(), "Average", node => true);

            nameBasedBinder.Register(new CountBinder(), "Count", node => true);
            nameBasedBinder.Register(new CountBinder(), "LongCount", node => true);

            nameBasedBinder.Register(new DistinctBinder(), "Distinct", node => node.Arguments.Count == 1);

            nameBasedBinder.Register(new FirstBinder(), "First", node => true);
            nameBasedBinder.Register(new FirstBinder(), "FirstOrDefault", node => true);

            nameBasedBinder.Register(new CorrelatedGroupByBinder(), "GroupBy", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));
            nameBasedBinder.Register(new GroupByWithResultSelectorBinder(), "GroupBy", node =>
                node.Arguments.Count == 3 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1) &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[2], 2));

            nameBasedBinder.Register(new MaxBinder(), "Max", node => true);

            nameBasedBinder.Register(new MinBinder(), "Min", node => true);

            nameBasedBinder.Register(new OfTypeBinder(), "OfType", node => true);

            var orderByBinder = new OrderByBinder();
            nameBasedBinder.Register(orderByBinder, "OrderBy", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));
            nameBasedBinder.Register(orderByBinder, "OrderByDescending", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));
            nameBasedBinder.Register(orderByBinder, "ThenBy", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));
            nameBasedBinder.Register(orderByBinder, "ThenByDescending", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));

            nameBasedBinder.Register(new SelectBinder(), "Select", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));

            nameBasedBinder.Register(new SingleBinder(), "Single", node => true);
            nameBasedBinder.Register(new SingleBinder(), "SingleOrDefault", node => true);

            nameBasedBinder.Register(new SkipBinder(), "Skip", node => true);

            nameBasedBinder.Register(new TakeBinder(), "Take", node => true);

            nameBasedBinder.Register(new SumBinder(), "Sum", node => true);

            nameBasedBinder.Register(new WhereBinder(), "Where", node =>
                node.Arguments.Count == 2 &&
                ExtensionExpressionVisitor.IsLambda(node.Arguments[1], 1));

            __methodCallBinder = nameBasedBinder;
        }

        public static Expression Bind(Expression node, IBsonSerializer documentSerializer, IBsonSerializerRegistry serializerRegistry)
        {
            // bind
            var binder = new ProjectionBinder(documentSerializer, serializerRegistry, __methodCallBinder);
            var bound = binder.Bind(node);

            // post-process
            return CorrelatedGroupRewriter.Rewrite(bound, serializerRegistry);
        }

        private readonly IBsonSerializer _documentSerializer;
        private readonly ProjectionBindingContext _context;
        private readonly IMethodCallBinder _methodCallBinder;

        private ProjectionBinder(IBsonSerializer documentSerializer, IBsonSerializerRegistry serializerRegistry, IMethodCallBinder methodCallBinder)
        {
            _methodCallBinder = Ensure.IsNotNull(methodCallBinder, "methodCallBinder");
            _documentSerializer = Ensure.IsNotNull(documentSerializer, "documentSerializer");
            _context = new ProjectionBindingContext(
                Ensure.IsNotNull(serializerRegistry, "serializerRegistry"),
                _methodCallBinder);
        }

        private Expression Bind(Expression node)
        {
            if (node.Type == typeof(void))
            {
                var message = string.Format("Expressions of type void are not supported: {0}", node.ToString());
                throw new NotSupportedException(message);
            }

            node = RemoveUnnecessaries(node);
            var methodCall = node as MethodCallExpression;

            if (methodCall != null)
            {
                return BuildFromMethodCall((MethodCallExpression)node);
            }

            return BuildFromNonMethodCall(node);
        }

        private ProjectionExpression BindProjection(Expression node)
        {
            var bound = Bind(node);
            var projection = bound as ProjectionExpression;
            if (projection == null)
            {
                var message = string.Format("Expected a ProjectionExpression, but found a {0} in the expression tree: {1}.",
                    bound.GetType(),
                    node.ToString());
                throw new NotSupportedException(message);
            }

            return projection;
        }

        private Expression BuildFromMethodCall(MethodCallExpression node)
        {
            Expression source;
            IEnumerable<Expression> arguments;

            if (node.Object != null)
            {
                source = node.Object;
                arguments = node.Arguments;
            }
            else
            {
                // assuming an extension method here...
                source = node.Arguments[0];
                arguments = node.Arguments.Skip(1);
            }

            var projection = BindProjection(source);
            var result = _methodCallBinder.Bind(projection, _context, node, arguments);
            if (result == null)
            {
                var message = string.Format("The method {0} is not supported in the expression tree: {1}.",
                    node.Method.Name,
                    node.ToString());
                throw new NotSupportedException(message);
            }

            return result;
        }

        private Expression BuildFromNonMethodCall(Expression node)
        {
            if (node.NodeType == ExpressionType.Constant &&
                node.Type.IsGenericType &&
                node.Type.GetGenericTypeDefinition() == typeof(IMongoQueryable<>))
            {
                var inputType = node.Type.GetGenericArguments()[0];
                var serializationInfo = new BsonSerializationInfo(
                    null,
                    _documentSerializer,
                    _documentSerializer.ValueType);

                return new ProjectionExpression(
                    new SerializationExpression(node, serializationInfo),
                    new SerializationExpression(Expression.Parameter(inputType, "document"), serializationInfo));
            }

            var message = string.Format("The expression tree is not supported: {0}",
                node.ToString());

            throw new NotSupportedException(message);
        }

        private Expression RemoveUnnecessaries(Expression node)
        {
            while (node.NodeType == ExpressionType.Convert ||
                node.NodeType == ExpressionType.ConvertChecked ||
                node.NodeType == ExpressionType.Quote)
            {
                node = ((UnaryExpression)node).Operand;
            }

            return node;
        }
    }
}