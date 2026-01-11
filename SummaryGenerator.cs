namespace KismetAnalyzer;

using System.Text.RegularExpressions;

using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

using Dot;
using static Dot.Html;

public class SummaryGenerator
{
    public class Lines
    {
        public List<Element> Elements { get; }
        public int Address { get; }
        public List<Lines> Children { get; }


        public Lines(int address = -1)
        {
            Elements = new List<Element>();
            Address = address;
            Children = new List<Lines>();
            if (address >= 0)
            {
                Elements.Add(new Element(ElementKind.Address, $"{address}: "));
            }
        }

        public Lines(string label) : this(-1)
        {
            Elements.Add(new Element(ElementKind.PlainText, label));
        }


        // Fluent builders for semantic elements
        public Lines Expr(KismetExpression expr) { Elements.Add(new Element(ElementKind.ExpressionType, "EX_" + expr.Inst)); return this; }
        public Lines Addr(uint addr) { Elements.Add(new Element(ElementKind.Address, addr.ToString())); return this; }
        public Lines Addr(int addr) { Elements.Add(new Element(ElementKind.Address, addr.ToString())); return this; }
        public Lines Var(string name) { Elements.Add(new Element(ElementKind.Variable, name)); return this; }
        public Lines Func(string name) { Elements.Add(new Element(ElementKind.Function, name)); return this; }
        public Lines Str(string value) { Elements.Add(new Element(ElementKind.StringLiteral, value)); return this; }
        public Lines Num(string value) { Elements.Add(new Element(ElementKind.NumericLiteral, value)); return this; }
        public Lines Num<T>(T value) where T : struct { Elements.Add(new Element(ElementKind.NumericLiteral, value.ToString()!)); return this; }
        public Lines Type(string name) { Elements.Add(new Element(ElementKind.TypeName, name)); return this; }
        public Lines Kw(string keyword) { Elements.Add(new Element(ElementKind.Keyword, keyword)); return this; }
        public Lines Text(string text) { Elements.Add(new Element(ElementKind.PlainText, text)); return this; }

        public Lines Add(Lines line)
        {
            Children.Add(line);
            return this;
        }
        public Lines Add(string line)
        {
            Children.Add(new Lines(line));
            return this;
        }

        // Returns elements for rendering
        public IEnumerable<(int Address, int Nest, List<Element> Elements)> GetElements()
        {
            yield return (Address, 0, Elements);
            foreach (var child in Children)
            {
                foreach (var (address, nest, elements) in child.GetElements())
                {
                    yield return (address, nest + 1, elements);
                }
            }
        }
    }
    public class Instruction
    {
        public uint Address { get; }
        public List<Reference> ReferencedAddresses { get; }
        public Lines Content { get; }

        public Instruction(uint address, List<Reference> referencedAddresses, Lines content)
        {
            Address = address;
            ReferencedAddresses = referencedAddresses;
            Content = content;
        }
    }
    public readonly struct Reference
    {
        public uint Address { get; }
        public ReferenceType Type { get; }
        public string? FunctionName { get; }

        public Reference(uint address, ReferenceType type, string? functionName = null)
        {
            Address = address;
            Type = type;
            FunctionName = functionName;
        }
    }
    public enum ReferenceType
    {
        Normal,
        Jump,
        JumpTrue,
        JumpFalse,
        Push,
        Pop,
        Latent,
        Function,
    }

    public class BasicBlock
    {
        public uint StartAddress { get; }
        public List<Instruction> Instructions { get; }
        public List<BlockEdge> Successors { get; }
        public bool IsTerminal { get; set; }

        public BasicBlock(uint startAddress)
        {
            StartAddress = startAddress;
            Instructions = new List<Instruction>();
            Successors = new List<BlockEdge>();
            IsTerminal = false;
        }

        public uint EndAddress => Instructions.Count > 0
            ? Instructions[^1].Address
            : StartAddress;
    }

    public readonly struct BlockEdge
    {
        public uint TargetAddress { get; }
        public ReferenceType Type { get; }
        public string? FunctionName { get; }

        public BlockEdge(uint targetAddress, ReferenceType type, string? functionName = null)
        {
            TargetAddress = targetAddress;
            Type = type;
            FunctionName = functionName;
        }
    }

    UAsset Asset;
    TextWriter Output;
    TextWriter DotOutput;
    Graph Graph;
    FunctionExport? CurrentFunction;

    public SummaryGenerator(UAsset asset, TextWriter output, TextWriter dotOutput)
    {
        Asset = asset;
        Output = output;
        DotOutput = dotOutput;

        Graph = new Graph("digraph");
        Graph.GraphAttributes["fontname"] = "monospace";
        Graph.GraphAttributes["pack"] = "true";
        Graph.NodeAttributes["fontname"] = "monospace";
        Graph.EdgeAttributes["fontname"] = "monospace";
    }

    public bool Summarize()
    {
        var anyExport = false;

        var classExport = Asset.GetClassExport();
        if (classExport != null)
        {
            anyExport = true;
            var classLines = new Lines().Kw("ClassExport:").Text(" ").Type(classExport.ObjectName.ToString());
            classLines.Add(new Lines().Kw("SuperStruct:").Text(" ").Type(ToString(classExport.SuperStruct)));
            var propLines = new Lines().Kw("Properties:");

            foreach (var (propType, propName, propFlags) in GetProperties(classExport))
            {
                var lines = new Lines().Type(propType).Text(" ").Var(propName);

                var flags = new List<string>();
                foreach (EPropertyFlags flag in Enum.GetValues(typeof(EPropertyFlags)))
                {
                    if (flag != EPropertyFlags.CPF_None && propFlags.HasFlag(flag))
                    {
                        flags.Add(flag.ToString());
                    }
                }
                if (flags.Count > 0) lines.Add(new Lines().Kw(String.Join("|", flags)));
                propLines.Add(lines);
            }
            classLines.Add(propLines);

            var classNode = new Node(classExport.ObjectName.ToString());
            var classCell = Td().Attr("ALIGN", "LEFT").Attr("BALIGN", "LEFT");
            DotHtmlLabelRenderer.RenderTo(classCell, classLines);
            classNode.HtmlLabel = Table("#d4ffd4").Add(Tr().Add(classCell));
            classNode.Attributes["shape"] = "none";
            Graph.Nodes.Add(classNode);
        }
        else
        {
            Output.WriteLine("No ClassExport");
        }

        // Pre-pass: Collect all instructions and cross-function entry points
        var allFunctionInstructions = new Dictionary<string, List<Instruction>>();
        var externalEntryPoints = new Dictionary<string, HashSet<uint>>();

        foreach (var export in Asset.Exports)
        {
            if (export is FunctionExport e)
            {
                CurrentFunction = e;
                string functionName = e.ObjectName.ToString();
                var instructions = new List<Instruction>();
                uint index = 0;
                foreach (var exp in e.ScriptBytecode)
                {
                    instructions.Add(Stringify(exp, ref index));
                }
                allFunctionInstructions[functionName] = instructions;

                // Collect cross-function entry points (calls into other functions with int arg)
                foreach (var instr in instructions)
                {
                    foreach (var reference in instr.ReferencedAddresses)
                    {
                        if (reference.Type == ReferenceType.Function && reference.FunctionName != null)
                        {
                            if (!externalEntryPoints.ContainsKey(reference.FunctionName))
                            {
                                externalEntryPoints[reference.FunctionName] = new HashSet<uint>();
                            }
                            externalEntryPoints[reference.FunctionName].Add(reference.Address);
                        }
                    }
                }
            }
        }

        // Set packmode based on total header nodes (functions + class)
        var headerCount = allFunctionInstructions.Count + (classExport != null ? 1 : 0);
        Graph.GraphAttributes["packmode"] = $"array_ti{headerCount}";

        foreach (var export in Asset.Exports)
        {
            if (export is FunctionExport e)
            {
                if (classExport == null) throw new InvalidOperationException("ClassExport found");
                anyExport = true;
                Output.WriteLine("FunctionExport " + e.ObjectName);

                string functionName = e.ObjectName.ToString();

                var functionLines = new Lines().Kw("Function").Text(" ").Func(functionName);
                foreach (var (propType, propName, propFlags) in GetProperties(e))
                {
                    var flags = new List<string>();
                    foreach (EPropertyFlags flag in Enum.GetValues(typeof(EPropertyFlags)))
                    {
                        if (flag != EPropertyFlags.CPF_None && propFlags.HasFlag(flag))
                        {
                            flags.Add(flag.ToString());
                        }
                    }
                    var propLines = new Lines().Type(propType).Text(" ").Var(propName);
                    if (flags.Count > 0) propLines.Add(new Lines().Kw(String.Join("|", flags)));
                    functionLines.Add(propLines);
                }

                var functionNode = new Node(functionName);
                var functionCell = Td().Attr("ALIGN", "LEFT").Attr("BALIGN", "LEFT");
                DotHtmlLabelRenderer.RenderTo(functionCell, functionLines);
                functionNode.HtmlLabel = Table("#ffd4d4").Add(Tr().Add(functionCell));
                functionNode.Attributes["shape"] = "none";
                Graph.Nodes.Add(functionNode);

                var instructions = allFunctionInstructions[functionName];

                // Get external entry points for this function (calls from other functions)
                var externalLeaders = externalEntryPoints.GetValueOrDefault(functionName) ?? new HashSet<uint>();

                // Build basic blocks with both internal and external leaders
                var leaders = IdentifyBlockLeaders(instructions, functionName);
                leaders.UnionWith(externalLeaders);
                var blocks = BuildBasicBlocks(instructions, leaders);

                // Build address-to-block mapping for edge resolution
                var addressToBlock = new Dictionary<uint, BasicBlock>();
                foreach (var block in blocks)
                {
                    addressToBlock[block.StartAddress] = block;
                }

                // Function entry edge to first block
                if (blocks.Count > 0)
                {
                    var functionEdge = new Edge(functionName, $"{functionName}__block_{blocks[0].StartAddress}");
                    functionEdge.BCompass = "n";
                    Graph.Edges.Add(functionEdge);
                }

                // Generate TXT and DOT output for each block
                foreach (var block in blocks)
                {
                    // Skip blocks that only contain EX_EndOfScript
                    if (block.Instructions.Count == 1 &&
                        block.Instructions[0].Content.Elements.Any(e =>
                            e.Kind == ElementKind.ExpressionType && e.Value == "EX_EndOfScript"))
                    {
                        continue;
                    }

                    // TXT output with full block headers
                    Output.WriteLine($"=== Block @ {block.StartAddress} ===");
                    Output.WriteLine($"    Successors: {FormatSuccessors(block.Successors)}");
                    Output.WriteLine();

                    PlainTextRenderer.RenderTo(Output, block.Instructions.Select(i => i.Content));
                    Output.WriteLine();

                    // DOT output: one node per block
                    var nodeId = $"{functionName}__block_{block.StartAddress}";
                    var node = new Node(nodeId);

                    // Build label with header and content cells
                    var headerCell = Td().Add($"{classExport.ObjectName}::{functionName} @ {block.StartAddress}");
                    var contentCell = Td().Attr("ALIGN", "LEFT").Attr("BALIGN", "LEFT");
                    DotHtmlLabelRenderer.RenderTo(contentCell, block.Instructions.Select(i => i.Content));

                    node.HtmlLabel = Table("#eeeeee")
                        .Add(Tr().Add(headerCell))
                        .Add(Tr().Add(contentCell));
                    node.Attributes["shape"] = "none";
                    Graph.Nodes.Add(node);

                    // Create edges based on block successors
                    foreach (var successor in block.Successors)
                    {
                        string targetNodeId;

                        if (successor.FunctionName != null)
                        {
                            // Cross-function reference
                            targetNodeId = $"{successor.FunctionName}__block_{successor.TargetAddress}";
                        }
                        else if (addressToBlock.TryGetValue(successor.TargetAddress, out var targetBlock))
                        {
                            // Local block reference
                            targetNodeId = $"{functionName}__block_{targetBlock.StartAddress}";
                        }
                        else
                        {
                            // Target might be in the middle of a block - find containing block
                            var containingBlock = blocks.FirstOrDefault(b =>
                                b.Instructions.Any(i => i.Address == successor.TargetAddress));
                            if (containingBlock != null)
                            {
                                targetNodeId = $"{functionName}__block_{containingBlock.StartAddress}";
                            }
                            else
                            {
                                // Unknown target - create orphan edge
                                targetNodeId = $"{functionName}__addr_{successor.TargetAddress}";
                            }
                        }

                        var edge = new Edge(nodeId, targetNodeId);
                        edge.BCompass = "n";
                        switch (successor.Type)
                        {
                            case ReferenceType.Normal:
                                edge.Attributes["style"] = "solid";
                                break;
                            case ReferenceType.JumpTrue:
                                edge.Attributes["color"] = "green";
                                break;
                            case ReferenceType.JumpFalse:
                                edge.Attributes["color"] = "red";
                                edge.Attributes["arrowhead"] = "onormal";
                                edge.ACompass = "e";
                                break;
                            case ReferenceType.Jump:
                                edge.Attributes["color"] = "blue";
                                edge.Attributes["style"] = "dashed";
                                break;
                            case ReferenceType.Push:
                                edge.Attributes["color"] = "purple";
                                edge.Attributes["style"] = "dotted";
                                edge.Attributes["penwidth"] = "2";
                                edge.Attributes["arrowhead"] = "onormal";
                                edge.ACompass = "e";
                                break;
                            case ReferenceType.Latent:
                                edge.Attributes["color"] = "orange";
                                edge.Attributes["style"] = "dashed";
                                edge.Attributes["arrowhead"] = "onormal";
                                edge.ACompass = "e";
                                break;
                            case ReferenceType.Function:
                                edge.Attributes["color"] = "gray";
                                edge.Attributes["style"] = "dotted";
                                edge.Attributes["penwidth"] = "2";
                                edge.Attributes["arrowhead"] = "vee";
                                edge.ACompass = "e";
                                break;
                        }
                        Graph.Edges.Add(edge);
                    }
                }
            }
        }

        Graph.Write(DotOutput);
        return anyExport;
    }

    static HashSet<uint> IdentifyBlockLeaders(List<Instruction> instructions, string functionName)
    {
        var leaders = new HashSet<uint>();

        if (instructions.Count == 0) return leaders;

        // First instruction is always a leader
        leaders.Add(instructions[0].Address);

        // Count how many times each address is targeted by non-sequential jumps
        var targetCounts = new Dictionary<uint, int>();

        foreach (var instr in instructions)
        {
            foreach (var reference in instr.ReferencedAddresses)
            {
                // Skip Normal (fall-through) - it's sequential flow, not a branch
                if (reference.Type == ReferenceType.Normal)
                    continue;

                if (reference.FunctionName == null ||
                    (reference.Type == ReferenceType.Latent && reference.FunctionName == functionName))
                {
                    var addr = reference.Address;
                    targetCounts[addr] = targetCounts.GetValueOrDefault(addr, 0) + 1;

                    // Conditional branches and special flow always create leaders
                    // (they represent divergence points)
                    if (reference.Type == ReferenceType.JumpTrue ||
                        reference.Type == ReferenceType.JumpFalse ||
                        reference.Type == ReferenceType.Push ||
                        reference.Type == ReferenceType.Latent)
                    {
                        leaders.Add(addr);
                    }
                }
            }
        }

        // Addresses targeted by multiple non-sequential jumps are convergence points
        foreach (var (addr, count) in targetCounts)
        {
            if (count > 1)
            {
                leaders.Add(addr);
            }
        }

        return leaders;
    }

    static List<BasicBlock> BuildBasicBlocks(List<Instruction> instructions, HashSet<uint> leaders)
    {
        var blocks = new List<BasicBlock>();

        if (instructions.Count == 0) return blocks;

        // Build address -> instruction map for control flow traversal
        var byAddress = instructions.ToDictionary(i => i.Address);

        // Track visited addresses and queue of addresses to process
        var visited = new HashSet<uint>();
        var toVisit = new Queue<uint>();
        toVisit.Enqueue(instructions[0].Address);

        // Also queue all leaders (they may be jump targets not reachable sequentially)
        foreach (var leader in leaders)
            toVisit.Enqueue(leader);

        while (toVisit.Count > 0)
        {
            var startAddr = toVisit.Dequeue();
            if (visited.Contains(startAddr) || !byAddress.ContainsKey(startAddr))
                continue;

            var currentBlock = new BasicBlock(startAddr);
            blocks.Add(currentBlock);

            var currentAddr = startAddr;
            while (byAddress.TryGetValue(currentAddr, out var instr) && !visited.Contains(currentAddr))
            {
                // If we hit a leader (and it's not the start of this block), end block
                if (leaders.Contains(currentAddr) && currentAddr != startAddr)
                {
                    toVisit.Enqueue(currentAddr);
                    break;
                }

                visited.Add(currentAddr);
                currentBlock.Instructions.Add(instr);

                // Check if this is a block terminator (branching or special flow)
                if (IsBlockTerminator(instr))
                {
                    currentBlock.IsTerminal = IsAbsoluteTerminator(instr);
                    // Queue all branch targets
                    foreach (var r in instr.ReferencedAddresses.Where(r => r.FunctionName == null))
                        toVisit.Enqueue(r.Address);
                    break;
                }

                // Follow Normal reference (fall-through or unconditional jump target)
                var normalRef = instr.ReferencedAddresses
                    .FirstOrDefault(r => r.Type == ReferenceType.Normal && r.FunctionName == null);

                if (normalRef.Address != 0 || instr.ReferencedAddresses.Any(r => r.Type == ReferenceType.Normal && r.Address == 0))
                {
                    currentAddr = normalRef.Address;
                }
                else
                {
                    // No Normal reference - terminal instruction
                    currentBlock.IsTerminal = true;
                    break;
                }
            }
        }

        // Build successor edges for each block
        foreach (var block in blocks)
        {
            if (block.Instructions.Count == 0) continue;

            // Collect non-local references from ALL instructions in the block
            // (e.g., calls to ubergraph, latent action continuations, push execution flow)
            foreach (var instr in block.Instructions)
            {
                foreach (var reference in instr.ReferencedAddresses)
                {
                    if (reference.Type == ReferenceType.Function ||
                        reference.Type == ReferenceType.Latent ||
                        reference.Type == ReferenceType.Push)
                    {
                        block.Successors.Add(new BlockEdge(
                            reference.Address,
                            reference.Type,
                            reference.FunctionName));
                    }
                }
            }

            // Control flow successors come from the last instruction
            var lastInstr = block.Instructions[^1];
            foreach (var reference in lastInstr.ReferencedAddresses)
            {
                // Skip types already added above, and skip Pop (indirect jump with no known target)
                if (reference.Type != ReferenceType.Function &&
                    reference.Type != ReferenceType.Latent &&
                    reference.Type != ReferenceType.Push &&
                    reference.Type != ReferenceType.Pop)
                {
                    block.Successors.Add(new BlockEdge(
                        reference.Address,
                        reference.Type,
                        reference.FunctionName));
                }
            }
        }

        return blocks;
    }

    static bool IsBlockTerminator(Instruction instr)
    {
        // Block terminates when control flow branches or has special flow
        // - JumpTrue/JumpFalse = conditional branch
        // - Push/Latent = special flow (latent actions, push execution flow)
        // - Pop = indirect jump to previously pushed address
        // - No Normal reference = no fall-through (terminal instructions)
        return instr.ReferencedAddresses.Any(r =>
            r.Type == ReferenceType.JumpTrue ||
            r.Type == ReferenceType.JumpFalse ||
            r.Type == ReferenceType.Push ||
            r.Type == ReferenceType.Pop ||
            r.Type == ReferenceType.Latent) ||
            !instr.ReferencedAddresses.Any(r => r.Type == ReferenceType.Normal);
    }

    static bool IsAbsoluteTerminator(Instruction instr)
    {
        // No fall-through = no Normal reference
        return !instr.ReferencedAddresses.Any(r => r.Type == ReferenceType.Normal);
    }

    static string FormatSuccessors(List<BlockEdge> successors)
    {
        return string.Join(", ", successors.Select(s =>
        {
            var target = s.FunctionName != null ? $"{s.FunctionName}:{s.TargetAddress}" : s.TargetAddress.ToString();
            return s.Type == ReferenceType.Normal ? target : $"{target} ({s.Type})";
        }));
    }

    Instruction Stringify(KismetExpression exp, ref uint index)
    {
        var referencedAddresses = new List<Reference>();
        var address = index;
        var content = Stringify(exp, ref index, referencedAddresses, true);
        return new Instruction(address, referencedAddresses, content);
    }

    Lines Stringify(KismetExpression exp, ref uint index, List<Reference> referencedAddresses, bool top = false)
    {
        int addr = (int)index;
        index++;
        Lines lines;
        switch (exp)
        {
            case EX_PushExecutionFlow e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Addr(e.PushingAddress);
                    referencedAddresses.Add(new Reference(e.PushingAddress, ReferenceType.Push));
                    index += 4;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ComputedJump e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.CodeOffsetExpression, ref index, referencedAddresses));
                    // TODO how to map out where this jumps?
                    break;
                }
            case EX_Jump e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Addr(e.CodeOffset);
                    // Normal reference - single target, like fall-through but to non-sequential address
                    referencedAddresses.Add(new Reference(e.CodeOffset, ReferenceType.Normal));
                    index += 4;
                    break;
                }
            case EX_JumpIfNot e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Addr(e.CodeOffset);
                    referencedAddresses.Add(new Reference(e.CodeOffset, ReferenceType.JumpFalse));
                    index += 4;
                    lines.Add(Stringify(e.BooleanExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.JumpTrue));
                    break;
                }
            case EX_LocalVariable e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Variable));
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LocalOutVariable e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Variable));
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_DefaultVariable e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Variable));
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_InstanceVariable e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Variable));
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ObjToInterfaceCast e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.ClassPtr));
                    index += 8;
                    lines.Add(Stringify(e.Target, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_InterfaceToObjCast e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.ClassPtr));
                    index += 8;
                    lines.Add(Stringify(e.Target, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Let e:
                {
                    lines = new Lines(addr).Expr(e);
                    index += 8;
                    lines.Add(Stringify(e.Variable, ref index, referencedAddresses));
                    lines.Add(Stringify(e.Expression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LetObj e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.VariableExpression, ref index, referencedAddresses));
                    lines.Add(Stringify(e.AssignmentExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LetDelegate e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.VariableExpression, ref index, referencedAddresses));
                    lines.Add(Stringify(e.AssignmentExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LetBool e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.VariableExpression, ref index, referencedAddresses));
                    lines.Add(Stringify(e.AssignmentExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LetWeakObjPtr e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.VariableExpression, ref index, referencedAddresses));
                    lines.Add(Stringify(e.AssignmentExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LetValueOnPersistentFrame e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.DestinationProperty));
                    index += 8;
                    lines.Add(Stringify(e.AssignmentExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_StructMemberContext e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.StructMemberExpression));
                    index += 8;
                    lines.Add(Stringify(e.StructExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_MetaCast e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.ClassPtr));
                    index += 8;
                    lines.Add(Stringify(e.Target, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_DynamicCast e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.ClassPtr));
                    index += 8;
                    lines.Add(Stringify(e.Target, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_PrimitiveCast e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(e.ConversionType.ToString());
                    index++;
                    switch (e.ConversionType)
                    {
                        case ECastToken.InterfaceToBool:
                            {
                                break;
                            }
                        case ECastToken.ObjectToBool:
                            {
                                break;
                            }
                        case ECastToken.ObjectToInterface:
                            {
                                index += 8;
                                // TODO InterfaceClass
                                break;
                            }
                    }
                    lines.Add(Stringify(e.Target, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_PopExecutionFlow e:
                {
                    lines = new Lines(addr).Expr(e);
                    referencedAddresses.Add(new Reference(uint.MaxValue, ReferenceType.Pop));
                    break;
                }
            case EX_PopExecutionFlowIfNot e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.BooleanExpression, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(uint.MaxValue, ReferenceType.Pop));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.JumpTrue));
                    break;
                }
            case EX_CallMath e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(ToString(e.StackNode));
                    index += 8;
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SwitchValue e:
                {
                    lines = new Lines(addr).Expr(e);
                    index += 6;
                    lines.Add(Stringify(e.IndexTerm, ref index, referencedAddresses));
                    // lines.Add("OffsetToSwitchEnd = " + e.EndGotoOffset);
                    var ci = -1;
                    foreach (var c in e.Cases)
                    {
                        ci++;
                        var nested = new Lines((int)addr).Kw("case").Text(" ").Num(ci).Text(":");
                        nested.Add(Stringify(c.CaseIndexValueTerm, ref index, referencedAddresses));
                        index += 4;
                        // nested.Add("NextCaseOffset = " + c.NextOffset);
                        nested.Add(Stringify(c.CaseTerm, ref index, referencedAddresses));
                        lines.Add(nested);
                    }
                    var defaultCase = new Lines((int)addr).Kw("default").Text(":");
                    defaultCase.Add(Stringify(e.DefaultTerm, ref index, referencedAddresses));
                    lines.Add(defaultCase);
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Self e:
                {
                    lines = new Lines(addr).Expr(e);
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_TextConst e:
                {
                    lines = new Lines(addr).Expr(e);
                    index++;
                    switch (e.Value.TextLiteralType)
                    {
                        case EBlueprintTextLiteralType.Empty:
                            {
                                break;
                            }
                        case EBlueprintTextLiteralType.LocalizedText:
                            {
                                lines.Add(new Lines().Kw("SourceString").Text(" = ").Str(ReadString(e.Value.LocalizedSource, ref index)));
                                lines.Add(new Lines().Kw("LocalizedKey").Text(" = ").Str(ReadString(e.Value.LocalizedKey, ref index)));
                                lines.Add(new Lines().Kw("LocalizedNamespace").Text(" = ").Str(ReadString(e.Value.LocalizedNamespace, ref index)));
                                break;
                            }
                        case EBlueprintTextLiteralType.InvariantText:
                            {
                                lines.Add(new Lines().Kw("SourceString").Text(" = ").Str(ReadString(e.Value.InvariantLiteralString, ref index)));
                                break;
                            }
                        case EBlueprintTextLiteralType.LiteralString:
                            {
                                lines.Add(new Lines().Kw("SourceString").Text(" = ").Str(ReadString(e.Value.LiteralString, ref index)));
                                break;
                            }
                        case EBlueprintTextLiteralType.StringTableEntry:
                            {
                                index += 8;
                                lines.Add(new Lines().Kw("TableId").Text(" = ").Str(ReadString(e.Value.StringTableId, ref index)));
                                lines.Add(new Lines().Kw("TableKey").Text(" = ").Str(ReadString(e.Value.StringTableKey, ref index)));
                                break;
                            }
                    }
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ObjectConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Value));
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_VectorConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num($"{e.Value.X},{e.Value.Y},{e.Value.Z}");
                    index += Asset.ObjectVersionUE5 >= ObjectVersionUE5.LARGE_WORLD_COORDINATES ? 24U : 12U;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Vector3fConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num($"{e.X},{e.Y},{e.Z}");
                    index += 12;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_RotationConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num($"{e.Value.Pitch},{e.Value.Yaw},{e.Value.Roll}");
                    index += Asset.ObjectVersionUE5 >= ObjectVersionUE5.LARGE_WORLD_COORDINATES ? 24U : 12U;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_TransformConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ")
                        .Num($"[Rot={e.Value.Rotation.X},{e.Value.Rotation.Y},{e.Value.Rotation.Z},{e.Value.Rotation.W}] [Pos={e.Value.Translation.X},{e.Value.Translation.Y},{e.Value.Translation.Z}] [Scale={e.Value.Scale3D.X},{e.Value.Scale3D.Y},{e.Value.Scale3D.Z}]");
                    index += Asset.ObjectVersionUE5 >= ObjectVersionUE5.LARGE_WORLD_COORDINATES ? 80U : 40U;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Context e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.ObjectExpression, ref index, referencedAddresses));
                    index += 4;
                    //lines.Add("SkipOffsetForNull = " + e.Offset);
                    index += 8;
                    lines.Add(Stringify(e.ContextExpression, ref index, referencedAddresses));
                    //lines.Add("RValue = " + ToString(e.RValuePointer));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_CallMulticastDelegate e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(ToString(e.StackNode));
                    index += 8;
                    //lines.Add("IsSelfContext = " + (Asset.GetClassExport().OuterIndex.Index == e.StackNode.Index));
                    lines.Add(Stringify(e.Delegate, ref index, referencedAddresses));
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LocalFinalFunction e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(ToString(e.StackNode));
                    index += 8;
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;

                    if (e.Parameters.Length == 1 && e.Parameters[0] is EX_IntConst value)
                    {
                        if (e.StackNode.IsExport())
                        {
                            referencedAddresses.Add(new Reference((uint)value.Value, ReferenceType.Function, e.StackNode.ToExport(Asset).ObjectName.ToString()));
                        }
                        else
                        {
                            Console.Error.WriteLine("WARN: Unimplemented StackNode import");
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("WARN: Unimplemented non-EX_IntConst");
                    }

                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_FinalFunction e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(ToString(e.StackNode));
                    index += 8;
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_LocalVirtualFunction e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(e.VirtualFunctionName.ToString());
                    index += 12;
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_VirtualFunction e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(e.VirtualFunctionName.ToString());
                    index += 12;
                    foreach (var arg in e.Parameters)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;

                    if (e.Parameters.Length == 1 && e.Parameters[0] is EX_IntConst value)
                    {
                        if (Asset.Exports.Any(ex => ex is FunctionExport && ex.ObjectName.ToString() == e.VirtualFunctionName.ToString()))
                        {
                            referencedAddresses.Add(new Reference((uint)value.Value, ReferenceType.Function, e.VirtualFunctionName.ToString()));
                        }
                    }

                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_AddMulticastDelegate e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.Delegate, ref index, referencedAddresses));
                    lines.Add(Stringify(e.DelegateToAdd, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_RemoveMulticastDelegate e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.Delegate, ref index, referencedAddresses));
                    lines.Add(Stringify(e.DelegateToAdd, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ClearMulticastDelegate e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.DelegateToClear, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_BindDelegate e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Func(e.FunctionName.ToString());
                    index += 12;
                    lines.Add(Stringify(e.Delegate, ref index, referencedAddresses));
                    lines.Add(Stringify(e.ObjectTerm, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_StructConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.Struct));
                    index += 8;
                    index += 4;
                    foreach (var arg in e.Value)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;

                    if (e.Struct.IsImport())
                    {
                        var s = e.Struct.ToImport(Asset);
                        if (s.ObjectName.ToString() == "LatentActionInfo" && s.ClassPackage.ToString() == "/Script/CoreUObject")
                        {
                            if (e.Value.Length != 4)
                            {
                                throw new Exception("Struct LatentActionInfo should have 4 members");
                            }
                            if (e.Value[0] is EX_SkipOffsetConst skip)
                            {
                                if (e.Value[2] is EX_NameConst name)
                                {
                                    if (e.Value[3] is EX_Self self)
                                    {
                                        referencedAddresses.Add(new Reference(skip.Value, ReferenceType.Latent, name.Value.ToString()));
                                    }
                                    else
                                    {
                                        Console.Error.WriteLine("WARN: Unimplemented LatentActionInfo to other than EX_Self");
                                    }
                                }
                                else
                                {
                                    Console.Error.WriteLine("WARN: Expected EX_NameConst but found " + e.Value[2]);
                                }
                            }
                            else
                            {
                                // can be EX_IntConst -1 which i guess means it doesn't go anywhere?
                                // Console.Error.WriteLine("WARN: Expected EX_SkipOffsetConst but found " + e.Value[0]);
                            }
                        }
                    }

                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SetArray e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.AssigningProperty, ref index, referencedAddresses));
                    foreach (var arg in e.Elements)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SetSet e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.SetProperty, ref index, referencedAddresses));
                    index += 4;
                    foreach (var arg in e.Elements)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SetMap e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.MapProperty, ref index, referencedAddresses));
                    index += 4;
                    var ei = -1;
                    foreach (var pair in Pairs(e.Elements))
                    {
                        ei++;
                        var entry = new Lines((int)addr).Kw("entry").Text(" ").Num(ei).Text(":");
                        lines.Add(Stringify(pair.Item1, ref index, referencedAddresses));
                        lines.Add(Stringify(pair.Item2, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_MapConst e:
                {
                    lines = new Lines(addr).Expr(e);
                    index += 8;
                    lines.Add(new Lines().Kw("KeyProperty").Text(": ").Type(ToString(e.KeyProperty)));
                    lines.Add(new Lines().Kw("ValueProperty").Text(": ").Type(ToString(e.ValueProperty)));
                    index += 4;
                    var ei = -1;
                    foreach (var pair in Pairs(e.Elements))
                    {
                        ei++;
                        var entry = new Lines().Kw("entry").Text(" ").Num(ei).Text(":");
                        lines.Add(Stringify(pair.Item1, ref index, referencedAddresses));
                        lines.Add(Stringify(pair.Item2, ref index, referencedAddresses));
                    }
                    index++;
                    break;
                }
            case EX_SoftObjectConst e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.Value, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_BitFieldConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(ToString(e.Property)).Text(" ").Num(e.Value);
                    index += 8 + 1;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ByteConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_IntConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 4;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_NothingInt32 e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 4;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Int64Const e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_UInt64Const e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_FloatConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 4;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_DoubleConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Num(e.Value);
                    index += 8;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_NameConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Var(e.Value.ToString());
                    index += 12;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_StringConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Str(e.Value);
                    index += 1 + (uint)e.Value.Length;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_UnicodeStringConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Str(e.Value);
                    index += (uint)(e.Value.Length + 1) * 2;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_ArrayConst e:
                {
                    lines = new Lines(addr).Expr(e);
                    index += 8;
                    lines.Add(new Lines().Type(ToString(e.InnerProperty)));
                    index += 4;
                    foreach (var arg in e.Elements)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SetConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Type(ToString(e.InnerProperty));
                    index += 8;
                    index += 4;
                    foreach (var arg in e.Elements)
                    {
                        lines.Add(Stringify(arg, ref index, referencedAddresses));
                    }
                    index++;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_SkipOffsetConst e:
                {
                    lines = new Lines(addr).Expr(e).Text(" ").Addr(e.Value);
                    // handled in LatentActionInfo struct instead
                    // referencedAddresses.Add(new Reference(e.Value, ReferenceType.Skip));
                    index += 4;
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Return e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.ReturnExpression, ref index, referencedAddresses));
                    break;
                }
            case EX_InterfaceContext e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.InterfaceValue, ref index, referencedAddresses));
                    break;
                }
            case EX_ArrayGetByRef e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.ArrayVariable, ref index, referencedAddresses));
                    lines.Add(Stringify(e.ArrayIndex, ref index, referencedAddresses));
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_Tracepoint e:
                {
                    lines = new Lines(addr).Expr(e);
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_WireTracepoint e:
                {
                    lines = new Lines(addr).Expr(e);
                    if (top) referencedAddresses.Add(new Reference(index, ReferenceType.Normal));
                    break;
                }
            case EX_FieldPathConst e:
                {
                    lines = new Lines(addr).Expr(e);
                    lines.Add(Stringify(e.Value, ref index, referencedAddresses));
                    break;
                }
            case EX_True:
            case EX_False:
            case EX_Nothing:
            case EX_NoObject:
            case EX_NoInterface:
            case EX_EndOfScript:
                {
                    lines = new Lines(addr).Expr(exp);
                    break;
                }
            default:
                {
                    throw new NotImplementedException($"DBG missing expression {exp}");
                    // lines = new Lines("DBG missing expression " + exp); // TODO better error handling
                    // break;
                }
        }
        return lines;
    }

    static IEnumerable<(KismetExpression, KismetExpression)> Pairs(IEnumerable<KismetExpression> input)
    {
        var e = input.GetEnumerator();
        try
        {
            while (e.MoveNext())
            {
                var a = e.Current;
                if (e.MoveNext())
                {
                    yield return (a, e.Current);
                }
            }
        }
        finally
        {
            (e as IDisposable)?.Dispose();
        }
    }

    static string ReadString(KismetExpression exp, ref uint index)
    {
        index++;
        switch (exp)
        {
            case EX_StringConst e:
                {
                    index += (uint)e.Value.Length + 1;
                    return e.Value;
                }
            case EX_UnicodeStringConst e:
                {
                    index += 2 * ((uint)e.Value.Length + 1);
                    return e.Value;
                }
            default:
                Console.Error.WriteLine("WARN: ReadString called on non-string");
                return "ERR";
        }
    }

    string ToString(KismetPropertyPointer pointer)
    {
        if (pointer.Old != null)
        {
            return ToString(pointer.Old);
        }
        if (pointer.New != null)
        {
            return ToString(pointer.New.Path);
        }
        throw new Exception("Unreachable");
    }

    string ToString(FPackageIndex index)
    {
        string getChain(FPackageIndex child)
        {
            if (child.IsNull())
            {
                return "";
            }
            if (child.IsImport())
            {
                var a = child.ToImport(Asset);
                return $"{getChain(a.OuterIndex)}{a.ObjectName}->";
            }
            if (child.IsExport())
            {
                var a = child.ToExport(Asset);
                return $"{getChain(a.OuterIndex)}{a.ObjectName}->";
            }
            throw new NotImplementedException("Unreachable");

        }
        if (index.IsNull())
        {
            return "null";
        }
        else if (index.IsExport())
        {
            var exp = index.ToExport(Asset);
            // Shorten variable names that belong to the current function or class
            if (CurrentFunction != null)
            {
                var functionIndex = Asset.Exports.IndexOf(CurrentFunction) + 1;
                if (exp.OuterIndex.Index == functionIndex)
                {
                    return exp.ObjectName.ToString();
                }
            }
            var classExport = Asset.GetClassExport();
            if (classExport != null)
            {
                var classIndex = Asset.Exports.IndexOf(classExport) + 1;
                if (exp.OuterIndex.Index == classIndex)
                {
                    return exp.ObjectName.ToString();
                }
            }
            return $"export {getChain(exp.OuterIndex)}{exp.ObjectName}";
        }
        else if (index.IsImport())
        {
            return $"import {getChain(index.ToImport(Asset).OuterIndex)}{index.ToImport(Asset).ObjectName}";
        }
        throw new Exception("Unreachable");
    }

    static void PrintIndent(TextWriter Output, int indent, string str, string prefix = "")
    {
        Output.WriteLine((indent == 0 ? prefix : "").PadRight((indent + 2) * 4) + str);
    }

    static string ToString(FName[] arr)
    {
        return "[" + String.Join(",", (object[])arr) + "]";
    }

    IEnumerable<(string Type, string Name, EPropertyFlags Flags)> GetProperties(StructExport structExport)
    {
        // New format (UE 4.25+): properties are in LoadedProperties as FProperty
        if (structExport.LoadedProperties.Length > 0)
        {
            foreach (var prop in structExport.LoadedProperties)
            {
                yield return (prop.SerializedType.ToString(), prop.Name.ToString(), prop.PropertyFlags);
            }
        }
        // Old format (pre-4.25): properties are UObjects in Children
        else if (structExport.Children != null)
        {
            foreach (var childIndex in structExport.Children)
            {
                if (childIndex.IsExport())
                {
                    var childExport = childIndex.ToExport(Asset);
                    if (childExport is PropertyExport propExport)
                    {
                        var typeName = childExport.GetExportClassType().ToString().Replace("Property", "");
                        yield return (typeName, childExport.ObjectName.ToString(), propExport.Property.PropertyFlags);
                    }
                }
            }
        }
    }
}
