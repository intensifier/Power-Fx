﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Linq;
using Microsoft.PowerFx.Core.Localization;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Syntax.SourceInformation;

namespace Microsoft.PowerFx.Syntax
{
    /// <summary>
    /// Base class for all parse nodes.
    /// </summary>
    [ThreadSafeImmutable]
    public abstract class TexlNode
    {
        private TexlNode _parent;
        private bool? _usesChains;
        private protected int _depth;

        internal readonly int Id;

        internal int MinChildID { get; private protected set; }

        internal readonly Token Token;

        internal SourceList SourceList { get; private set; }

        internal int Depth => _depth;

        /// <summary>
        /// Parent <see cref="TexlNode" /> in the AST structure (if any).
        /// </summary>
        public TexlNode Parent
        {
            get => _parent;
            internal set
            {
                Contracts.Assert(_parent == null);
                _parent = value;
            }
        }

        internal bool UsesChains
        {
            get
            {
                if (_usesChains.HasValue)
                {
                    return _usesChains.Value;
                }

                _usesChains = ChainTrackerVisitor.Run(this);
                return _usesChains.Value;
            }
        }

        private protected TexlNode(ref int idNext, Token primaryToken, SourceList sourceList)
        {
            Contracts.Assert(idNext >= 0);
            Contracts.AssertValue(primaryToken);
            Contracts.AssertValue(sourceList);

            Id = idNext++;
            MinChildID = Id;
            Token = primaryToken;
            SourceList = sourceList;
            _depth = 1;
        }

        internal abstract TexlNode Clone(ref int idNext, Span ts);

        /// <summary>
        /// Accept a visitor <see cref="TexlVisitor" />.
        /// </summary>
        /// <param name="visitor">The visitor to accept.</param>
        public abstract void Accept(TexlVisitor visitor);

        /// <summary>
        /// Accept a functional visitor <see cref="TexlFunctionalVisitor{TResult, TContext}" />.
        /// </summary>
        /// <typeparam name="TResult">The result type of the visitor.</typeparam>
        /// <typeparam name="TContext">The context type of the visitor.</typeparam>
        /// <param name="visitor">The functional visitor to accept.</param>
        /// <param name="context">The context to pass to the visitor.</param>
        /// <returns>The result of the visitor.</returns>
        public abstract TResult Accept<TResult, TContext>(TexlFunctionalVisitor<TResult, TContext> visitor, TContext context);

        /// <summary>
        /// Kind of the parse node.
        /// </summary>
        public abstract NodeKind Kind { get; }

        internal void Parser_SetSourceList(SourceList sources)
        {
            Contracts.AssertValue(sources);
            SourceList = sources;
        }

        // TODO: Comment - what are the differences between different spans defined here?
        // TODO: Should we keep this internal?

        /// <summary>
        /// Gets the text span associated with the current token.
        /// </summary>
        /// <remarks>The returned <see cref="Span"/> is constructed using the minimum and limit values  of
        /// the token's span. Ensure that the token is valid before calling this method.</remarks>
        /// <returns>A <see cref="Span"/> representing the range of text covered by the token.</returns>
        public virtual Span GetTextSpan()
        {
            return new Span(Token.VerifyValue().Span.Min, Token.VerifyValue().Span.Lim);
        }

        // TODO: Comment - what are the differences between different spans defined here?
        // TODO: Should we keep this internal?

        /// <summary>
        /// Gets the complete span of the current object, including all relevant text.
        /// </summary>
        /// <remarks>The returned <see cref="Span"/> is constructed based on the text span of the current
        /// object.</remarks>
        /// <returns>A <see cref="Span"/> object representing the complete span of the current object.</returns>
        public virtual Span GetCompleteSpan()
        {
            return new Span(GetTextSpan());
        }

        // TODO: Comment - what are the differences between different spans defined here?
        // TODO: Should we keep this internal?

        /// <summary>
        /// Calculates the span that encompasses all tokens in the source list.
        /// </summary>
        /// <remarks>If the source list contains no tokens, the method returns the complete span.
        /// Otherwise, it determines the span based on the minimum position of the first token  and the limit position
        /// of the last token in the source list.</remarks>
        /// <returns>A <see cref="Span"/> representing the range of all tokens in the source list,  or the complete span if the
        /// source list is empty.</returns>
        public Span GetSourceBasedSpan()
        {
            if (SourceList.Tokens.Count() == 0)
            {
                return GetCompleteSpan();
            }

            var start = SourceList.Tokens.First().Span.Min;
            var end = SourceList.Tokens.Last().Span.Lim;
            return new Span(start, end);
        }

        internal virtual ErrorNode AsError()
        {
            return null;
        }

        internal virtual FirstNameNode CastFirstName()
        {
            Contracts.Assert(false);
            return (FirstNameNode)this;
        }

        internal virtual FirstNameNode AsFirstName()
        {
            return null;
        }

        internal virtual ParentNode AsParent()
        {
            return null;
        }

        internal virtual SelfNode AsSelf()
        {
            return null;
        }

        internal virtual DottedNameNode CastDottedName()
        {
            Contracts.Assert(false);
            return (DottedNameNode)this;
        }

        internal virtual DottedNameNode AsDottedName()
        {
            return null;
        }

        internal virtual NumLitNode AsNumLit()
        {
            return null;
        }

        internal virtual DecLitNode AsDecLit()
        {
            return null;
        }

        internal virtual StrLitNode AsStrLit()
        {
            return null;
        }

        internal virtual StrInterpNode AsStrInterp()
        {
            return null;
        }

        internal virtual BoolLitNode CastBoolLit()
        {
            Contracts.Assert(false);
            return (BoolLitNode)this;
        }

        internal virtual BoolLitNode AsBoolLit()
        {
            return null;
        }

        internal virtual UnaryOpNode CastUnaryOp()
        {
            Contracts.Assert(false);
            return (UnaryOpNode)this;
        }

        internal virtual UnaryOpNode AsUnaryOpLit()
        {
            return null;
        }

        internal virtual BinaryOpNode CastBinaryOp()
        {
            Contracts.Assert(false);
            return (BinaryOpNode)this;
        }

        internal virtual BinaryOpNode AsBinaryOp()
        {
            return null;
        }

        internal virtual VariadicOpNode AsVariadicOp()
        {
            return null;
        }

        internal virtual ListNode CastList()
        {
            Contracts.Assert(false);
            return (ListNode)this;
        }

        internal virtual ListNode AsList()
        {
            return null;
        }

        internal virtual CallNode CastCall()
        {
            Contracts.Assert(false);
            return (CallNode)this;
        }

        internal virtual CallNode AsCall()
        {
            return null;
        }

        internal virtual RecordNode CastRecord()
        {
            Contracts.Assert(false);
            return (RecordNode)this;
        }

        internal virtual RecordNode AsRecord()
        {
            return null;
        }

        internal virtual TableNode AsTable()
        {
            return null;
        }

        internal virtual BlankNode AsBlank()
        {
            return null;
        }

        internal virtual AsNode AsAsNode()
        {
            return null;
        }

        internal bool InTree(TexlNode root)
        {
            Contracts.AssertValue(root);
            return root.MinChildID <= Id && Id <= root.Id;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return TexlPretty.PrettyPrint(this);
        }

        // Returns the nearest node to the cursor position. If the node has child nodes returns the nearest child node.
        internal static TexlNode FindNode(TexlNode rootNode, int cursorPosition)
        {
            Contracts.AssertValue(rootNode);
            Contracts.Assert(cursorPosition >= 0);

            return FindNodeVisitor.Run(rootNode, cursorPosition);
        }

        internal TexlNode FindTopMostDottedParentOrSelf()
        {
            var parent = this;

            while (parent != null && parent.Parent != null && parent.Parent.Kind == NodeKind.DottedName)
            {
                parent = parent.Parent;
            }

            return parent;
        }
    }
}
