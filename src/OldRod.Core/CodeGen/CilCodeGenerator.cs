// Project OldRod - A KoiVM devirtualisation utility.
// Copyright (C) 2019 Washi
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using OldRod.Core.Architecture;
using OldRod.Core.Ast.Cil;
using OldRod.Core.CodeGen.Blocks;
using OldRod.Core.Disassembly.ControlFlow;
using OldRod.Core.Disassembly.DataFlow;
using Rivers;
using Rivers.Analysis;
using Rivers.Analysis.Connectivity;

namespace OldRod.Core.CodeGen
{
    public class CilCodeGenerator : ICilAstVisitor<IList<CilInstruction>>
    {
        private const string InvalidAstMessage =
            "The provided CIL AST is invalid or incomplete. " +
            "This might be because the IL to CIL recompiler contains a bug. " +
            "For more details, inspect the control flow graphs generated by the recompiler.";

        private readonly CilAstFormatter _formatter;
        private readonly CodeGenerationContext _context;

        private IDictionary<Node, CilInstruction> _blockEntries;
        private IDictionary<Node, CilInstruction> _blockExits;
        
        public CilCodeGenerator(CodeGenerationContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _formatter = new CilAstFormatter();
        }
        
        public IList<CilInstruction> VisitCompilationUnit(CilCompilationUnit unit)
        {
            // Add variable signatures to the end result.
            BindVariablesToSignatures(unit);
            
            var result = GenerateInstructions(unit);

            var instructions = new CilInstructionCollection(_context.MethodBody);
            instructions.AddRange(result);
            instructions.CalculateOffsets();
            
            CreateExceptionHandlers(unit, instructions);

            return instructions;
        }

        private void BindVariablesToSignatures(CilCompilationUnit unit)
        {
            foreach (var variable in unit.Variables)
                _context.Variables.Add(variable, new CilLocalVariable(variable.VariableType));

            foreach (var parameter in unit.Parameters)
            {
                var physicalParameter = _context.MethodBody.Owner.Parameters
                    .GetBySignatureIndex(parameter.ParameterIndex);
                
                if (physicalParameter != _context.MethodBody.Owner.Parameters.ThisParameter)
                    physicalParameter.ParameterType = parameter.VariableType;

                _context.Parameters.Add(parameter, physicalParameter);
            }
        }

        private IList<CilInstruction> GenerateInstructions(CilCompilationUnit unit)
        {
            // Define block headers to use as branch targets later.
            foreach (var node in unit.ControlFlowGraph.Nodes)
                _context.BlockHeaders[node] = new CilInstruction(CilOpCodes.Nop);

            var generator = new BlockGenerator(unit.ControlFlowGraph, this);
            var rootScope = generator.CreateBlock();

            var result = rootScope.GenerateInstructions();

            _blockEntries = generator.BlockEntries;
            _blockExits = generator.BlockExits;

            return result;
        }

        private void CreateExceptionHandlers(CilCompilationUnit unit, CilInstructionCollection result)
        {
            foreach (var subGraph in unit.ControlFlowGraph.SubGraphs)
            {
                var ehFrame = (EHFrame) subGraph.UserData[EHFrame.EHFrameProperty];
                
                CilExceptionHandlerType type;
                switch (ehFrame.Type)
                {
                    case EHType.CATCH:
                        type = CilExceptionHandlerType.Exception;
                        break;
                    case EHType.FILTER:
                        type = CilExceptionHandlerType.Filter;
                        break;
                    case EHType.FAULT:
                        type = CilExceptionHandlerType.Fault;
                        break;
                    case EHType.FINALLY:
                        type = CilExceptionHandlerType.Finally;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // Find first and last nodes of try block.
                var tryBody = (ICollection<Node>) subGraph.UserData[ControlFlowGraph.TryBlockProperty];
                var (tryStartNode, tryEndNode) = FindMinMaxNodes(tryBody);
                
                // Find first and last nodes of handler block.
                var handlerBody = (ICollection<Node>) subGraph.UserData[ControlFlowGraph.HandlerBlockProperty];
                var (handlerStartNode, handlerEndNode) = FindMinMaxNodes(handlerBody);

                // Create handler.
                var handler = new CilExceptionHandler
                {
                    HandlerType = type,
                    TryStart = new CilInstructionLabel(_blockEntries[tryStartNode]),
                    TryEnd = new CilInstructionLabel(
                        result.GetByOffset(_blockExits[tryEndNode].Offset + _blockExits[tryEndNode].Size)
                        ?? throw new CilCodeGeneratorException(
                            $"Could not infer end of try block in {_context.MethodBody.Owner.Name}.")),
                    HandlerStart = new CilInstructionLabel(_blockEntries[handlerStartNode]),
                    HandlerEnd = new CilInstructionLabel(
                        result.GetByOffset(_blockExits[handlerEndNode].Offset + _blockExits[handlerEndNode].Size)
                        ?? throw new CilCodeGeneratorException(
                            $"Could not infer end of handler block in {_context.MethodBody.Owner.Name}.")),
                    ExceptionType = ehFrame.CatchType
                };
                
                _context.ExceptionHandlers.Add(ehFrame, handler);
            }
        }

        private static (Node minNode, Node maxNode) FindMinMaxNodes(ICollection<Node> nodes)
        {
            Node minNode = null;
            Node maxNode = null;
            int minOffset = int.MaxValue;
            int maxOffset = -1;
            foreach (var node in nodes)
            {
                var block = (CilAstBlock) node.UserData[CilAstBlock.AstBlockProperty];
                if (block.BlockHeader.Offset < minOffset)
                {
                    minNode = node;
                    minOffset = block.BlockHeader.Offset;
                }
                
                if (block.BlockHeader.Offset > maxOffset)
                {
                    maxNode = node;
                    maxOffset = block.BlockHeader.Offset;
                }
            }

            return (minNode, maxNode);
        }

        public IList<CilInstruction> VisitBlock(CilAstBlock block)
        {
            var result = new List<CilInstruction>();
            result.Add(block.BlockHeader);
            foreach (var statement in block.Statements)
                result.AddRange(statement.AcceptVisitor(this));
            return result;
        }

        public IList<CilInstruction> VisitExpressionStatement(CilExpressionStatement statement)
        {
            return statement.Expression.AcceptVisitor(this);
        }

        public IList<CilInstruction> VisitAssignmentStatement(CilAssignmentStatement statement)
        {
            var result = new List<CilInstruction>();
            result.AddRange(statement.Value.AcceptVisitor(this));
            result.Add(new CilInstruction(CilOpCodes.Stloc, _context.Variables[statement.Variable]));
            return result;
        }

        public IList<CilInstruction> VisitInstructionExpression(CilInstructionExpression expression)
        {
            var result = new List<CilInstruction>();

            // Sanity check for expression validity. 
            ValidateExpression(expression);

            // Decide whether to emit FL updates or not.
            if (expression.ShouldEmitFlagsUpdate)
            {
                var first = expression.Arguments[0];

                switch (expression.Arguments.Count)
                {
                    case 1:
                        result.AddRange(_context.BuildFlagAffectingExpression32(
                            first.AcceptVisitor(this),
                            expression.Instructions,
                            _context.Constants.GetFlagMask(expression.AffectedFlags), 
                            expression.ExpressionType != null));
                        break;
                    case 2:
                        var second = expression.Arguments[1];
                        
                        result.AddRange(_context.BuildFlagAffectingExpression32(
                            first.AcceptVisitor(this),
                            second.AcceptVisitor(this),
                            expression.Instructions,
                            _context.Constants.GetFlagMask(expression.AffectedFlags), 
                            expression.InvertedFlagsUpdate,
                            expression.ExpressionType != null));
                        break;
                }
            }
            else
            {
                foreach (var argument in expression.Arguments)
                    result.AddRange(argument.AcceptVisitor(this));
                result.AddRange(expression.Instructions);
            }
            
            return result;
        }

        public IList<CilInstruction> VisitUnboxToVmExpression(CilUnboxToVmExpression expression)
        {
            var result = new List<CilInstruction>(expression.Expression.AcceptVisitor(this));
            
            if (expression.Type.IsTypeOf("System", "Object"))
            {
                var convertMethod = _context.VmHelperType.Methods.First(x =>
                    x.Name == nameof(VmHelper.ConvertToVmType)
                    && x.Parameters.Count == 1);
                
                result.Add(new CilInstruction(CilOpCodes.Call, convertMethod));
            }
            else
            {
                var convertMethod = _context.VmHelperType.Methods.First(x =>
                    x.Name == nameof(VmHelper.ConvertToVmType)
                    && x.Parameters.Count == 2);
                
                var typeFromHandle = _context.ReferenceImporter.ImportMethod(typeof(Type).GetMethod("GetTypeFromHandle"));
                result.AddRange(new[]
                {
                    new CilInstruction(CilOpCodes.Ldtoken, expression.Type),
                    new CilInstruction(CilOpCodes.Call, typeFromHandle), 
                    new CilInstruction(CilOpCodes.Call, convertMethod),
                });
            }
            
            return result;
        }

        public IList<CilInstruction> VisitVariableExpression(CilVariableExpression expression)
        {
            CilInstruction instruction;
            if (expression.IsParameter)
            {
                instruction = new CilInstruction(expression.IsReference
                        ? CilOpCodes.Ldarga
                        : CilOpCodes.Ldarg,
                    _context.Parameters[(CilParameter) expression.Variable]);
            }
            else
            {
                instruction = new CilInstruction(expression.IsReference
                        ? CilOpCodes.Ldloca
                        : CilOpCodes.Ldloc,
                    _context.Variables[expression.Variable]);
            }

            return new[]
            {
                instruction
            };
        }

        private void ValidateExpression(CilInstructionExpression expression)
        {
            int stackSize = expression.Arguments.Count;
            foreach (var instruction in expression.Instructions)
            {
                stackSize += instruction.GetStackPopCount(_context.MethodBody);
                if (stackSize < 0)
                {
                    throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                        $"Insufficient arguments are pushed onto the stack'{expression.AcceptVisitor(_formatter)}'."));
                }

                stackSize += instruction.GetStackPushCount();

                ValidateInstruction(expression, instruction);
            }
        }

        private void ValidateInstruction(CilInstructionExpression expression, CilInstruction instruction)
        {
            switch (instruction.OpCode.OperandType)
            {
                case CilOperandType.ShortInlineBrTarget:
                case CilOperandType.InlineBrTarget:
                    if (!(instruction.Operand is CilInstruction))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a branch target operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineMethod:
                case CilOperandType.InlineField:
                case CilOperandType.InlineType:
                case CilOperandType.InlineTok:
                    if (!(instruction.Operand is IMemberDescriptor))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a member reference operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineSig:
                    if (!(instruction.Operand is StandAloneSignature))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a signature operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineI:
                    if (!(instruction.Operand is int))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int32 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineI8:
                    if (!(instruction.Operand is long))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int64 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineNone:
                    if (instruction.Operand != null)
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Unexpected operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                case CilOperandType.InlineR:
                    if (!(instruction.Operand is double))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a float64 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.ShortInlineI:
                    if (!(instruction.Operand is sbyte))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int8 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.ShortInlineR:
                    if (!(instruction.Operand is float))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a float32 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineString:
                    if (!(instruction.Operand is string))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a string operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineSwitch:
                    if (!(instruction.Operand is IList<CilInstruction>))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a switch table operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                case CilOperandType.ShortInlineVar:
                case CilOperandType.InlineVar:
                    if (!(instruction.Operand is CilLocalVariable))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a variable operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineArgument:
                case CilOperandType.ShortInlineArgument:
                    if (!(instruction.Operand is CilLocalVariable))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a parameter operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                default:
                    throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                        $"Unexpected opcode in '{expression.AcceptVisitor(_formatter)}'."));
            }
        }
        
    }
}