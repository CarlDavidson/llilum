//
// Copyright (c) Microsoft Corporation.    All rights reserved.
//

namespace Microsoft.Zelig.CodeGeneration.IR.DataFlow
{
    using System;
    using System.Collections.Generic;

    using Microsoft.Zelig.Runtime.TypeSystem;


    //
    // WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!!
    // WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!!
    //
    // This algorithm is currently broken, due to the removal of UsedBy and DefinedBy properties from the Expression class.
    //
    // WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!!
    // WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!! WARNING!!!
    //
    //
    // This class computes, for each program point, the set of definitions that reach it.
    //
    // Here "definition" means "lvalue assignment".
    //
    // There are some type of variable assignment that are not tracked.
    // For example, when a variable is passed as an out/ref parameter to a method.
    // it's a possible assignment, but it cannot be tracked, due to chosen representation (one bit per program point).
    //
    public class ReachingDefinitions
    {
        //
        // State
        //

        BasicBlock[] m_basicBlocks;
        Operator[]   m_definitions;

        BitVector[]  m_state_RCHin;  // Definitions reaching the beginning of a basic block.
        BitVector[]  m_state_RCHout; // Definitions reaching the end of a basic block.
        BitVector[]  m_state_PRSV;   // Definitions preserved by a basic block.
        BitVector[]  m_state_GEN;    // Definitions generated by a basic block.

        //
        // Constructor Methods
        //

        private ReachingDefinitions( BasicBlock[] basicBlocks ,
                                     Operator[]   definitions )
        {
            m_basicBlocks = basicBlocks;
            m_definitions = definitions;
        }

        public static void Compute(     BasicBlock[] basicBlocks                   ,
                                        Operator[]   definitions                   ,
                                        bool         fComputeAtOperatorGranularity ,
                                    out BitVector[]  rch                           )
        {
            ReachingDefinitions reachdef = new ReachingDefinitions( basicBlocks, definitions );

            reachdef.Compute();

            if(fComputeAtOperatorGranularity)
            {
                rch = reachdef.ConvertToOperatorGranularity();
            }
            else
            {
                rch = reachdef.m_state_RCHin;
            }
        }

        //--//

        private void Compute()
        {
            int numBB = m_basicBlocks.Length;

            m_state_RCHin  = AllocateBitVectors( numBB );
            m_state_RCHout = AllocateBitVectors( numBB );
            m_state_PRSV   = AllocateBitVectors( numBB );
            m_state_GEN    = AllocateBitVectors( numBB );

            ComputeEquationParameters();

            SolveEquations();
        }

        private BitVector[] ConvertToOperatorGranularity()
        {
            GrowOnlySet< Operator > setPossibleDefinitions = SetFactory.NewWithReferenceEquality< Operator >();
            GrowOnlySet< Operator > setVisited             = SetFactory.NewWithReferenceEquality< Operator >();
            int                     numBB                  = m_basicBlocks.Length;
            int                     numOp                  = m_definitions.Length;

            BitVector[] res  = AllocateBitVectors( numOp );
            BitVector   prsv = new BitVector( numOp );
            BitVector   gen  = new BitVector( numOp );

            for(int i = 0; i < numBB; i++)
            {
                BitVector rch = m_state_RCHin[i];

                foreach(Operator op in m_basicBlocks[i].Operators)
                {
                    prsv.SetRange( 0, numOp );
                    gen .ClearAll();

                    ComputeStateUpdateForOperator( op, prsv, gen, setPossibleDefinitions, setVisited );

                    BitVector rchOp = res[ op.SpanningTreeIndex ];

                    rchOp.Assign( rch );

                    rch.AndInPlace( prsv );
                    rch.OrInPlace ( gen  );
                }
            }

            return res;
        }

        //--//

        private BitVector[] AllocateBitVectors( int size )
        {
            return BitVector.AllocateBitVectors( size, m_definitions.Length );
        }

        private void ComputeEquationParameters()
        {
            GrowOnlySet< Operator > setPossibleDefinitions = SetFactory.NewWithReferenceEquality< Operator >();
            GrowOnlySet< Operator > setVisited             = SetFactory.NewWithReferenceEquality< Operator >();
            int                     numBB                  = m_basicBlocks.Length;
            int                     numOp                  = m_definitions.Length;

            for(int i = 0; i < numBB; i++)
            {
                BitVector prsv = m_state_PRSV[i];
                BitVector gen  = m_state_GEN [i];

                prsv.SetRange( 0, numOp );
                gen .ClearAll();

                foreach(Operator op in m_basicBlocks[i].Operators)
                {
                    ComputeStateUpdateForOperator( op, prsv, gen, setPossibleDefinitions, setVisited );
                }
            }
        }

        private void ComputeStateUpdateForOperator( Operator              op                     ,
                                                    BitVector             prsv                   ,
                                                    BitVector             gen                    ,
                                                    GrowOnlySet<Operator> setPossibleDefinitions ,
                                                    GrowOnlySet<Operator> setVisited             )
        {
            //
            // We should consider all the side effects of an operator.
            //
            // For example:
            //
            //     - indirect load/store can affect a variable.
            //     - method call could affect all fields, arrays.
            //     - out/ref parameters could affect all fields, arrays, local variables, arguments, etc.
            //
            if(op.MayWriteThroughPointerOperands)
            {
                if(op is CallOperator)
                {
                    foreach(Expression ex2 in op.Arguments)
                    {
                        if(ex2.Type is ManagedPointerTypeRepresentation)
                        {
                            InvalidatePointer( ex2, setPossibleDefinitions, setVisited, prsv, gen );
                        }
                    }
                }
                else if(op is StoreIndirectOperator ||
                        op is StoreInstanceFieldOperator)
                {
                    InvalidatePointer( op.FirstArgument, setPossibleDefinitions, setVisited, prsv, gen );
                }
                else
                {
                    throw new NotImplementedException();
                }
            }

            foreach(var ex in op.Results)
            {
                InvalidateExpression( prsv, gen, ex );

                gen.Set( op.SpanningTreeIndex );
            }
        }

        private void InvalidatePointer( Expression            ex                     ,
                                        GrowOnlySet<Operator> setPossibleDefinitions ,
                                        GrowOnlySet<Operator> setVisited             ,
                                        BitVector             prsv                   ,
                                        BitVector             gen                    )
        {
            setPossibleDefinitions.Clear();
            setVisited            .Clear();

            //ex.CollectPossibleDefinitionSites( setPossibleDefinitions, setVisited );

            foreach(Operator op in setPossibleDefinitions)
            {
                if(prsv.IsEmpty)
                {
                    //
                    // Nothing preserved, no more changes possible.
                    //
                    break;
                }

                if(op is AddressAssignmentOperator)
                {
                    InvalidateExpression( prsv, gen, op.FirstArgument );
                }
                else if(op is StackAllocationOperator)
                {
                }
                else if(op is CallOperator)
                {
                }
                else if(op is LoadIndirectOperator)
                {
                }
                else if(op is SingleAssignmentOperator ||
                        op is PiOperator               ||
                        op is UnboxOperator             )
                {
                    Expression src = op.FirstArgument;

                    if(src.Type is PointerTypeRepresentation)
                    {
                        GrowOnlySet< Operator > setPossibleDefinitions2 = SetFactory.NewWithReferenceEquality< Operator >();
                        GrowOnlySet< Operator > setVisited2             = SetFactory.NewWithReferenceEquality< Operator >();

                        InvalidatePointer( src, setPossibleDefinitions2, setVisited2, prsv, gen );
                    }
                }
                else if(op is BinaryOperator     ||
                        op is SignExtendOperator ||
                        op is ZeroExtendOperator ||
                        op is TruncateOperator    )
                {
                    //
                    // This means there's some pointer arithmetic.
                    // As a consequence, we don't know the exact origin of the pointer.
                    //
                    prsv.ClearAll();
                    gen .ClearAll();
                }
                else if(op is LoadElementAddressOperator)
                {
                    //
                    // Although all accesses to arrays could be affected,
                    // we are only interested in the effects on expressions (locals, arguments, and temporaries).
                    //
                }
                else if(op is LoadElementOperator ||
                        op is LoadFieldOperator    )
                {
                    //
                    // Pointers stored in an object cannot point to locals or arguments, only to the heap.
                    //
                }
                else if(op is LoadAddressOperator)
                {
                    //
                    // Although all accesses to the field could be affected,
                    // we are only interested in the effects on expressions (locals, arguments, and temporaries).
                    //
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
        }

        private void InvalidateExpression( BitVector  prsv ,
                                           BitVector  gen  ,
                                           Expression ex   )
        {
            int numOp = m_definitions.Length;

            for(int i = 0; i < numOp; i++)
            {
                Operator op = m_definitions[i];

                foreach(var lhs in op.Results)
                {
                    if(lhs == ex)
                    {
                        prsv.Clear( i );
                        gen .Clear( i );
                        break;
                    }
                }
            }
        }

////    private void InvalidateFieldAccesses( BitVector               prsv  ,
////                                          FieldVariableExpression field )
////    {
////        int numOp = m_definitions.Length;
////
////        for(int i = 0; i < numOp; i++)
////        {
////            Operator op = m_definitions[i];
////
////            foreach(Expression ex in op.Rhs)
////            {
////                if(ex is FieldVariableExpression)
////                {
////                    FieldVariableExpression field2 = (FieldVariableExpression)ex;
////
////                    if(field2.Field == field.Field)
////                    {
////                        prsv.Clear( i );
////                        break;
////                    }
////                }
////            }
////        }
////    }

        private void SolveEquations()
        {
            BitVector tmp   = new BitVector();
            int       numBB = m_basicBlocks.Length;

            while(true)
            {
                bool fDone = true;

                //
                // RCHout = (RCHin And PRSV) Or GEN
                //
                for(int i = 0; i < numBB; i++)
                {
                    tmp.Assign( m_state_RCHin[i] );

                    tmp.AndInPlace( m_state_PRSV[i] );

                    tmp.OrInPlace( m_state_GEN[i] );

                    if(m_state_RCHout[i] != tmp)
                    {
                        m_state_RCHout[i].Assign( tmp );

                        fDone = false;
                    }
                }

                if(fDone)
                {
                    break;
                }

                //
                // RCHin = Or { RCHout(<predecessors>) }
                //
                for(int i = 0; i < numBB; i++)
                {
                    BitVector b = m_state_RCHin[i];

                    b.ClearAll();

                    foreach(BasicBlockEdge edge in m_basicBlocks[i].Predecessors)
                    {
                        b.OrInPlace( m_state_RCHout[ edge.Predecessor.SpanningTreeIndex ] );
                    }
                }
            }
        }
    }
}
