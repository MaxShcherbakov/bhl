using System;
using System.IO;
using System.Collections.Generic;

namespace bhl {

public class ModuleCompiler : AST_Visitor
{
  AST ast;
  CompiledModule compiled;

  ModulePath module_path;
  public ModulePath Module {
    get {
      return module_path;
    }
  }

  GlobalScope globs;
  public GlobalScope Globs {
    get {
      return globs;
    }
  }

  LocalScope symbols;

  List<Const> constants = new List<Const>();
  public List<Const> Constants {
    get {
      return constants;
    }
  }

  List<Instruction> init = new List<Instruction>();
  List<Instruction> code = new List<Instruction>();
  List<Instruction> head = null;

  Stack<AST_Block> ctrl_blocks = new Stack<AST_Block>();
  Stack<AST_Block> loop_blocks = new Stack<AST_Block>();

  internal struct BlockJump
  {
    internal AST_Block block;
    internal Instruction jump_op;
  }
  List<BlockJump> non_patched_breaks = new List<BlockJump>();
  List<BlockJump> non_patched_continues = new List<BlockJump>();
  Dictionary<AST_Block, Instruction> continue_jump_markers = new Dictionary<AST_Block, Instruction>();

  List<AST_FuncDecl> func_decls = new List<AST_FuncDecl>();
  HashSet<AST_Block> block_has_defers = new HashSet<AST_Block>();

  internal struct Jump
  {
    internal Instruction jump_op;
    internal Instruction dst;
    internal int operand_idx;
  }
  List<Jump> jumps = new List<Jump>();

  Dictionary<string, int> func2ip = new Dictionary<string, int>();
  public Dictionary<string, int> Func2Ip {
    get {
      return func2ip;
    }
  }

  Dictionary<uint, string> imports = new Dictionary<uint, string>();
  
  static Dictionary<byte, Definition> opcode_decls = new Dictionary<byte, Definition>();

  public class Definition
  {
    public Opcodes name { get; }
     //each array item represents the size of the operand in bytes
    public int[] operand_width { get; }
    public int size { get; }

    public Definition(Opcodes name, params int[] operand_width)
    {
      this.name = name;
      this.operand_width = operand_width;

      size = 1;//op itself
      for(int i=0;i<operand_width.Length;++i)
        size += operand_width[i];
    }
  }

  public class Instruction
  {
    public Definition def { get; }
    public Opcodes op { get; }
    public int[] operands { get; }
    public int line_num;
    //It's set automatically during final code traversal
    //and reflects an actual absolute instruction byte position in
    //a module. Used for post patching of jump instructions.
    public int pos;

    public Instruction(Opcodes op)
    {
      def = LookupOpcode(op);
      this.op = op;
      operands = new int[def.operand_width.Length];
    }

    public Instruction SetOperand(int idx, int op_val)
    {
      if(def.operand_width == null || def.operand_width.Length <= idx)
        throw new Exception("Invalid operand for opcode:" + op + ", at index:" + idx);

      int width = def.operand_width[idx];

      switch(width)
      {
        case 1:
          if(op_val < sbyte.MinValue || op_val > sbyte.MaxValue)
            throw new Exception("Operand value(1 byte) is out of bounds: " + op_val);
        break;
        case 2:
          if(op_val < short.MinValue || op_val > short.MaxValue)
            throw new Exception("Operand value(2 bytes) is out of bounds: " + op_val);
        break;
        case 3:
          if(op_val < -8388607 || op_val > 8388607)
            throw new Exception("Operand value(3 bytes) is out of bounds: " + op_val);
        break;
        case 4:
        break;
        default:
          throw new Exception("Not supported operand width: " + width + " for opcode:" + op);
      }

      operands[idx] = op_val;

      return this;
    }
    
    public void Write(Bytecode code)
    {
      code.Write8((uint)op);

      for(int i=0;i<operands.Length;++i)
      {
        int op_val = operands[i];
        int width = def.operand_width[i];
        switch(width)
        {
          case 1:
            code.Write8((uint)op_val);
          break;
          case 2:
            code.Write16((uint)op_val);
          break;
          case 3:
            code.Write24((uint)op_val);
          break;
          case 4:
            code.Write32((uint)op_val);
          break;
          default:
            throw new Exception("Not supported operand width: " + width + " for opcode:" + op);
        }
      }
    }
  }

  static ModuleCompiler()
  {
    DeclareOpcodes();
  }

  public ModuleCompiler(GlobalScope globs = null, AST ast = null, ModulePath module_path = null)
  {
    this.globs = globs;
    this.symbols = new LocalScope(globs);
    this.ast = ast;
    if(module_path == null)
      module_path = new ModulePath("", "");
    this.module_path = module_path;

    UseCode();
  }

  //NOTE: public for testing purposes only
  public ModuleCompiler UseCode()
  {
    head = code;
    return this;
  }

  //NOTE: public for testing purposes only
  public ModuleCompiler UseInit()
  {
    head = init;
    return this;
  }

  public CompiledModule Compile()
  {
    if(compiled == null)
    {
      //special case for tests where we
      //don't have any AST
      if(ast != null)
        Visit(ast);

      byte[] init_bytes;
      byte[] code_bytes;
      Dictionary<int, int> ip2src_line;
      Bake(out init_bytes, out code_bytes, out ip2src_line);

      compiled = new CompiledModule(
        Module.name, 
        code_bytes,
        Constants, 
        Func2Ip, 
        init_bytes,
        ip2src_line
      );    
    }

    return compiled;
  }

  int GetCodeSize()
  {
    int size = 0;
    for(int i=0;i<head.Count;++i)
      size += head[i].def.size;
    return size;
  }

  int GetPosition(Instruction inst)
  {
    int pos = 0;
    bool found = false;
    for(int i=0;i<head.Count;++i)
    {
      var tmp = head[i];
      pos += tmp.def.size;
      if(inst == tmp)
      {
        found = true;
        break;
      }
    }
    if(!found)
      throw new Exception("Instruction was not found: " + inst.op);
    return pos;
  }

  int AddConstant(AST_Literal lt)
  {
    return AddConstant(new Const(lt));
  }

  int AddConstant(Const new_cn)
  {
    for(int i = 0 ; i < constants.Count; ++i)
    {
      var cn = constants[i];
      if(cn.IsEqual(new_cn))
        return i;
    }
    constants.Add(new_cn);
    return constants.Count-1;
  }

  //TODO: use implicit conversion in Const ctor?
  int AddConstant(string str)
  {
    return AddConstant(new Const(str));
  }

  static void DeclareOpcodes()
  {
    DeclareOpcode(
      new Definition(
        Opcodes.Constant,
        3/*const idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.And
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Or
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.BitAnd
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.BitOr
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Mod
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Add
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Sub
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Div
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Mul
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Equal
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.NotEqual
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.LT
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.LTE
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GT
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GTE
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.UnaryNot
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.UnaryNeg
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.DeclVar,
        1 /*local idx*/, 1 /*Val type*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ArgVar,
        1 /*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.SetVar,
        1 /*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetVar,
        1 /*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.SetGVar,
        3/*module name idx*/, 3/*idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetGVar,
        3/*module name idx*/, 3/*idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ArgRef,
        1 /*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.SetAttr,
        3/*class type idx*/, 2/*member idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.SetAttrInplace,
        3/*class type idx*/, 2/*member idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetAttr,
        3/*class type idx*/, 2/*member idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.RefAttr,
        3/*class type idx*/, 2/*member idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetFunc,
        3/*module ip*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetFuncNative,
        3/*globs idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.FuncPtrToTop,
        4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetFuncFromVar,
        1/*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.GetFuncImported,
        3/*module name idx*/, 3/*func name idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Call,
        3/*module ip*/, 4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.CallNative,
        3/*globs idx*/, 4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.CallImported,
        3/*module name idx*/, 3/*func name idx*/, 4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.CallMethodNative,
        2/*class member idx*/, 3/*type literal idx*/, 4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.CallPtr,
        4/*args bits*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Lambda,
        2/*rel.offset skip lambda pos*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.UseUpval,
        1/*upval src idx*/, 1/*local dst idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.InitFrame,
        1/*total local vars*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Block,
        1/*type*/, 2/*len*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Return
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ReturnVal,
        1/*returned amount*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Pop
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Jump,
        2 /*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.JumpZ,
        2/*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.JumpPeekZ,
        2/*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.JumpPeekNZ,
        2/*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Break,
        2 /*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Continue,
        2 /*rel.offset*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.DefArg,
        1/*local scope idx*/, 2/*jump out of def.arg calc pos*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.New,
        3/*type idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.TypeCast,
        3/*type idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Inc,
        1/*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Dec,
        1/*local idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ClassBegin,
        4/*type idx*/, 4/*parent type idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ClassMember,
        4/*name idx*/, 4/*type idx*/
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.ClassEnd
      )
    );
    DeclareOpcode(
      new Definition(
        Opcodes.Import,
        4/*name idx*/
      )
    );
  }

  static void DeclareOpcode(Definition def)
  {
    opcode_decls.Add((byte)def.name, def);
  }

  static public Definition LookupOpcode(Opcodes op)
  {
    Definition def;
    if(!opcode_decls.TryGetValue((byte)op, out def))
       throw new Exception("No such opcode definition: " + op);
    return def;
  }

  Instruction Peek()
  {
    return head[head.Count-1];
  }

  void Pop()
  {
    head.RemoveAt(head.Count-1);
  }

  Instruction Emit(Opcodes op, int[] operands = null, int line_num = 0)
  {
    var inst = new Instruction(op);
    for(int i=0;i<operands?.Length;++i)
      inst.SetOperand(i, operands[i]);
    inst.line_num = line_num;
    head.Add(inst);
    return inst;
  }

  //NOTE: for testing purposes only
  public ModuleCompiler EmitThen(Opcodes op, params int[] operands)
  {
    Emit(op, operands);
    return this;
  }

  //NOTE: returns jump instruction to be patched later
  Instruction EmitConditionPlaceholderAndBody(AST_Block ast, int idx)
  {
    //condition
    Visit(ast.children[idx]);
    var jump_op = Emit(Opcodes.JumpZ);
    Visit(ast.children[idx+1]);
    return jump_op;
  }

  void PatchBreaks(AST_Block block)
  {
    for(int i=non_patched_breaks.Count;i-- > 0;)
    {
      var npb = non_patched_breaks[i];
      if(npb.block == block)
      {
        AddJumpFromTo(npb.jump_op, Peek());
        non_patched_breaks.RemoveAt(i);
      }
    }
  }

  void PatchContinues(AST_Block block)
  {
    for(int i=non_patched_continues.Count;i-- > 0;)
    {
      var npc = non_patched_continues[i];
      if(npc.block == block)
      {
        AddJumpFromTo(npc.jump_op, continue_jump_markers[npc.block]);
        non_patched_continues.RemoveAt(i);
      }
    }
    continue_jump_markers.Clear();
  }

  void AddJumpFromTo(Instruction jump_op, Instruction dst, int operand_idx = 0)
  {
    jumps.Add(new Jump() {
      jump_op = jump_op,
      dst = dst,
      operand_idx = operand_idx
    });
  }

  void PatchJumps()
  {
    //1. let's calculate absolute positions of all instructions
    int pos = 0;
    for(int i=0;i<code.Count;++i)
    {
      pos += code[i].def.size;
      code[i].pos = pos; 
    }

    //2. let's patch jump candidates
    for(int i=0;i<jumps.Count;++i)
    {
      var jp = jumps[i];
      int offset = jp.dst.pos - jp.jump_op.pos;
      jp.jump_op.SetOperand(jp.operand_idx, offset);
    }
  }

  public void Bake(out byte[] init_bytes, out byte[] code_bytes, out Dictionary<int, int> ip2src_line)
  {
    PatchJumps();

    {
      var bytecode = new Bytecode();
      for(int i=0;i<init.Count;++i)
        init[i].Write(bytecode);
      init_bytes = bytecode.GetBytes();
    }

    {
      var bytecode = new Bytecode();
      for(int i=0;i<code.Count;++i)
        code[i].Write(bytecode);
      code_bytes = bytecode.GetBytes();
    }

    ip2src_line = new Dictionary<int, int>();
    int pos = 0;
    //TODO: should we take into account init code?
    for(int i=0;i<code.Count;++i)
    {
      pos += code[i].def.size;
      ip2src_line.Add(pos-1, code[i].line_num);
    }
  }

#region Visits

  public override void DoVisit(AST_Interim ast)
  {
    VisitChildren(ast);
  }

  public override void DoVisit(AST_Module ast)
  {
    VisitChildren(ast);
  }

  public override void DoVisit(AST_Import ast)
  {
    for(int i=0;i<ast.module_names.Count;++i)
    {
      imports.Add(ast.module_ids[i], ast.module_names[i]);
      int module_idx = AddConstant(ast.module_names[i]);

      UseInit();
      Emit(Opcodes.Import, new int[] { module_idx });
      UseCode();
    }
  }

  public override void DoVisit(AST_FuncDecl ast)
  {
    func_decls.Add(ast);
    int ip = GetCodeSize();
    func2ip.Add(ast.name, ip);
    Emit(Opcodes.InitFrame, new int[] { (int)ast.local_vars_num + 1/*cargs bits*/});
    VisitChildren(ast);
    Emit(Opcodes.Return);
    func_decls.RemoveAt(func_decls.Count-1);
  }

  public override void DoVisit(AST_LambdaDecl ast)
  {
    var lmbd_op = Emit(Opcodes.Lambda, new int[] { 0 /*patched later*/});
    //skipping lambda opcode
    func2ip.Add(ast.name, GetCodeSize());
    Emit(Opcodes.InitFrame, new int[] { (int)ast.local_vars_num + 1/*cargs bits*/});
    VisitChildren(ast);
    Emit(Opcodes.Return);
    AddJumpFromTo(lmbd_op, Peek());

    foreach(var p in ast.upvals)
      Emit(Opcodes.UseUpval, new int[]{(int)p.upsymb_idx, (int)p.symb_idx});
  }

  public override void DoVisit(AST_ClassDecl ast)
  {
    UseInit();

    var name = ast.Name();
    //TODO:?
    //CheckNameIsUnique(name);

    ClassSymbol parent = null;
    if(ast.nparent != 0)
      parent = symbols.Resolve(ast.ParentName()) as ClassSymbol;

    var cl = new ClassSymbolScript(name, ast, parent);
    symbols.Define(cl);

    Emit(Opcodes.ClassBegin, new int[] { AddConstant(name.s), (int)(parent == null ? -1 : AddConstant(parent.GetName().s)) });
    for(int i=0;i<ast.children.Count;++i)
    {
      var child = ast.children[i];
      var vd = child as AST_VarDecl;
      if(vd != null)
      {
        cl.Define(new FieldSymbolScript(vd.name, vd.type));
        Emit(Opcodes.ClassMember, new int[] { AddConstant(vd.type), AddConstant(vd.name) });
      }
    }
    Emit(Opcodes.ClassEnd);

    UseCode();
  }

  public override void DoVisit(AST_EnumDecl ast)
  {
    var name = ast.Name();
    //TODO:?
    //CheckNameIsUnique(name);

    var es = new EnumSymbolScript(name);
    symbols.Define(es);
  }

  public override void DoVisit(AST_Block ast)
  {
    switch(ast.type)
    {
      case EnumBlock.IF:

        //if()
        // ^-- to be patched with position out of 'if body'
        //{
        // ...
        // jmp_opcode
        // ^-- to be patched with position out of 'if body'
        //}
        //else if()
        // ^-- to be patched with position out of 'if body'
        //{
        // ...
        // jmp_opcode
        // ^-- to be patched with position out of 'if body'
        //}
        //else
        //{
        //}

        Instruction last_jmp_op = null;
        int i = 0;
        //NOTE: amount of children is even if there's no 'else'
        for(;i < ast.children.Count; i += 2)
        {
          //break if there's only 'else leftover' left
          if((i + 2) > ast.children.Count)
            break;

          var if_op = EmitConditionPlaceholderAndBody(ast, i);
          if(last_jmp_op != null)
            AddJumpFromTo(last_jmp_op, Peek());

          //check if uncoditional jump out of 'if body' is required,
          //it's required only if there are other 'else if' or 'else'
          if(ast.children.Count > 2)
          {
            last_jmp_op = Emit(Opcodes.Jump);
            AddJumpFromTo(if_op, Peek());
          }
          else
            AddJumpFromTo(if_op, Peek());
        }
        //check fo 'else leftover'
        if(i != ast.children.Count)
        {
          Visit(ast.children[i]);
          AddJumpFromTo(last_jmp_op, Peek());
        }
      break;
      case EnumBlock.WHILE:
      {
        if(ast.children.Count != 2)
         throw new Exception("Unexpected amount of children (must be 2)");

        loop_blocks.Push(ast);

        var begin_op = Peek();

        var cond_op = EmitConditionPlaceholderAndBody(ast, 0);

        //to the beginning of the loop
        var jump_op = Emit(Opcodes.Jump);
        AddJumpFromTo(jump_op, begin_op);

        //patch 'jump out of the loop' position
        AddJumpFromTo(cond_op, Peek());

        PatchContinues(ast);

        PatchBreaks(ast);

        loop_blocks.Pop();
      }
      break;
      case EnumBlock.FUNC:
      {
        VisitChildren(ast);
      }
      break;
      case EnumBlock.SEQ:
      case EnumBlock.PARAL:
      case EnumBlock.PARAL_ALL:
      {
        VisitControlBlock(ast);
      }
      break;
      case EnumBlock.DEFER:
      {
        VisitDefer(ast);
      }
      break;
      default:
        throw new Exception("Not supported block type: " + ast.type); 
    }
  }

  void VisitControlBlock(AST_Block ast)
  {
    var parent_block = ctrl_blocks.Count > 0 ? ctrl_blocks.Peek() : null;

    bool parent_is_paral =
      parent_block != null &&
      (parent_block.type == EnumBlock.PARAL ||
       parent_block.type == EnumBlock.PARAL_ALL);

    bool is_paral = 
      ast.type == EnumBlock.PARAL || 
      ast.type == EnumBlock.PARAL_ALL;

    ctrl_blocks.Push(ast);

    var block_op = Emit(Opcodes.Block, new int[] { (int)ast.type, 0/*patched later*/});

    for(int i=0;i<ast.children.Count;++i)
    {
      var child = ast.children[i];

      //NOTE: let's automatically wrap all children with sequence if 
      //      they are inside paral block
      if(is_paral && 
          (!(child is AST_Block child_block) || 
           (child_block.type != EnumBlock.SEQ && child_block.type != EnumBlock.DEFER)))
      {
        var seq_child = new AST_Block();
        seq_child.type = EnumBlock.SEQ;
        seq_child.children.Add(child);
        child = seq_child;
      }

      Visit(child);
    }

    ctrl_blocks.Pop();

    bool need_block = is_paral || parent_is_paral || block_has_defers.Contains(ast);

    if(need_block)
      AddJumpFromTo(block_op, Peek(), operand_idx: 1);
    else
      head.Remove(block_op); 
  }

  void VisitDefer(AST_Block ast)
  {
    var parent_block = ctrl_blocks.Count > 0 ? ctrl_blocks.Peek() : null;
    if(parent_block != null)
      block_has_defers.Add(parent_block);

    var block_op = Emit(Opcodes.Block, new int[] { (int)ast.type, 0/*patched later*/});
    VisitChildren(ast);
    AddJumpFromTo(block_op, Peek(), operand_idx: 1);
  }

  public override void DoVisit(AST_TypeCast ast)
  {
    VisitChildren(ast);
    Emit(Opcodes.TypeCast, new int[] { AddConstant(ast.type) });
  }

  public override void DoVisit(AST_New ast)
  {
    Emit(Opcodes.New, new int[] { AddConstant(ast.type) });
    VisitChildren(ast);
  }

  public override void DoVisit(AST_Inc ast)
  {
    Emit(Opcodes.Inc, new int[] { (int)ast.symb_idx });
  }

  public override void DoVisit(AST_Dec ast)
  {
    Emit(Opcodes.Dec, new int[] { (int)ast.symb_idx });
  }

  public override void DoVisit(AST_Call ast)
  {  
    switch(ast.type)
    {
      case EnumCall.VARW:
      {
        Emit(Opcodes.SetVar, new int[] {(int)ast.symb_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.VAR:
      {
        Emit(Opcodes.GetVar, new int[] {(int)ast.symb_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.GVAR:
      {
        uint module_idx = module_path.id;
        if(ast.nname2 != module_idx)
        {
          var import_name = imports[ast.nname2];
          module_idx = (uint)AddConstant(import_name);
        }
        int var_idx = AddConstant(ast.name);

        Emit(Opcodes.GetGVar, new int[] {(int)module_idx, var_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.FUNC:
      {
        VisitChildren(ast);
        var instr = EmitGetFuncAddr(ast);
        //let's optimize some primitive calls
        if(instr.op == Opcodes.GetFunc)
        {
          Pop();
          Emit(Opcodes.Call, new int[] {instr.operands[0], (int)ast.cargs_bits}, (int)ast.line_num);
        }
        else if(instr.op == Opcodes.GetFuncNative)
        {
          Pop();
          Emit(Opcodes.CallNative, new int[] {instr.operands[0], (int)ast.cargs_bits}, (int)ast.line_num);
        }
        else if(instr.op == Opcodes.GetFuncImported)
        {
          Pop();
          Emit(Opcodes.CallImported, new int[] {instr.operands[0], instr.operands[1], (int)ast.cargs_bits}, (int)ast.line_num);
        }
        else
          Emit(Opcodes.CallPtr, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
      }
      break;
      case EnumCall.MVAR:
      {
        if((int)ast.symb_idx == -1)
          throw new Exception("Member '" + ast.name + "' idx is not valid: " + ast.scope_type);

        VisitChildren(ast);

        Emit(Opcodes.GetAttr, new int[] { AddConstant(ast.scope_type), (int)ast.symb_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.MVARW:
      {
        if((int)ast.symb_idx == -1)
          throw new Exception("Member '" + ast.name + "' idx is not valid: " + ast.scope_type);

        VisitChildren(ast);

        Emit(Opcodes.SetAttr, new int[] { AddConstant(ast.scope_type), (int)ast.symb_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.MFUNC:
      {
        var class_symb = symbols.Resolve(ast.scope_type) as ClassSymbol;
        if(class_symb == null)
          throw new Exception("Class type not found: " + ast.scope_type);
        int memb_idx = class_symb.members.FindStringKeyIndex(ast.name);
        if(memb_idx == -1)
          throw new Exception("Member '" + ast.name + "' not found in class: " + ast.scope_type);

        VisitChildren(ast);
        
        Emit(Opcodes.CallMethodNative, new int[] {memb_idx, AddConstant(ast.scope_type)}, (int)ast.line_num);
      }
      break;
      case EnumCall.MVARREF:
      {
        if((int)ast.symb_idx == -1)
          throw new Exception("Member '" + ast.name + "' idx is not valid: " + ast.scope_type);

        VisitChildren(ast);

        Emit(Opcodes.RefAttr, new int[] { AddConstant(ast.scope_type), (int)ast.symb_idx}, (int)ast.line_num);
      }
      break;
      case EnumCall.ARR_IDX:
      {
        var arr_symb = symbols.Resolve(ast.scope_type) as ArrayTypeSymbol;
        Emit(Opcodes.CallMethodNative, new int[] { GenericArrayTypeSymbol.IDX_At, AddConstant(arr_symb.name.s), 0 }, (int)ast.line_num);
      }
      break;
      case EnumCall.ARR_IDXW:
      {
        var arr_symb = symbols.Resolve(ast.scope_type) as ArrayTypeSymbol;
        Emit(Opcodes.CallMethodNative, new int[] { GenericArrayTypeSymbol.IDX_SetAt, AddConstant(arr_symb.name.s), 0}, (int)ast.line_num);
      }
      break;
      case EnumCall.LMBD:
      {
        VisitChildren(ast);
        Emit(Opcodes.FuncPtrToTop, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
        Emit(Opcodes.CallPtr, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
      }
      break;
      case EnumCall.FUNC_VAR:
      {
        VisitChildren(ast);
        Emit(Opcodes.GetFuncFromVar, new int[] {(int)ast.symb_idx}, (int)ast.line_num);
        Emit(Opcodes.CallPtr, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
      }
      break;
      case EnumCall.FUNC_MVAR:
      {
        VisitChildren(ast);
        Emit(Opcodes.FuncPtrToTop, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
        Emit(Opcodes.CallPtr, new int[] {(int)ast.cargs_bits}, (int)ast.line_num);
      }
      break;
      case EnumCall.GET_ADDR:
      {
        EmitGetFuncAddr(ast);
      }
      break;
      default:
        throw new Exception("Not supported call: " + ast.type);
    }
  }

  Instruction EmitGetFuncAddr(AST_Call ast)
  {
    int offset;
    if(func2ip.TryGetValue(ast.name, out offset))
    {
      return Emit(Opcodes.GetFunc, new int[] {offset}, (int)ast.line_num);
    }
    else if(globs.Resolve(ast.name) is FuncSymbolNative fsymb)
    {
      int func_idx = globs.GetMembers().IndexOf(fsymb);
      if(func_idx == -1)
        throw new Exception("Func '" + ast.name + "' idx not found in symbols");
      return Emit(Opcodes.GetFuncNative, new int[] {(int)func_idx}, (int)ast.line_num);
    }
    else if(ast.nname2 != module_path.id)
    {
      var import_name = imports[ast.nname2];
      int module_idx = AddConstant(import_name);
      int func_idx = AddConstant(ast.name);

      return Emit(Opcodes.GetFuncImported, new int[] {module_idx, func_idx}, (int)ast.line_num);
    }
    else
      throw new Exception("Func '" + ast.name + "' code not found");
  }

  public override void DoVisit(AST_Return ast)
  {
    VisitChildren(ast);
    Emit(Opcodes.ReturnVal, new int[] { ast.num });
  }

  public override void DoVisit(AST_Break ast)
  {
    var jump_op = Emit(Opcodes.Break, new int[] { 0 /*patched later*/});
    non_patched_breaks.Add(
      new BlockJump() 
        { block = loop_blocks.Peek(), 
          jump_op = jump_op
        }
    );
  }

  public override void DoVisit(AST_Continue ast)
  {
    var loop_block = loop_blocks.Peek();
    if(ast.jump_marker)
    {
      continue_jump_markers.Add(loop_block, Peek());
    }
    else
    {
      var jump_op = Emit(Opcodes.Continue, new int[] { 0 /*patched later*/});
      non_patched_continues.Add(
        new BlockJump() 
          { block = loop_block, 
            jump_op = jump_op
          }
      );
    }
  }

  public override void DoVisit(AST_PopValue ast)
  {
    Emit(Opcodes.Pop);
  }

  public override void DoVisit(AST_Literal ast)
  {
    int literal_idx = AddConstant(ast);
    Emit(Opcodes.Constant, new int[] { literal_idx });
  }

  public override void DoVisit(AST_BinaryOpExp ast)
  {
    switch(ast.type)
    {
      case EnumBinaryOp.AND:
      {
        Visit(ast.children[0]);
        var jump_op = Emit(Opcodes.JumpPeekZ, new int[] { 0 /*patched later*/});
        Visit(ast.children[1]);
        Emit(Opcodes.And);
        AddJumpFromTo(jump_op, Peek());
      }
      break;
      case EnumBinaryOp.OR:
      {
        Visit(ast.children[0]);
        var jump_op = Emit(Opcodes.JumpPeekNZ, new int[] { 0 /*patched later*/});
        Visit(ast.children[1]);
        Emit(Opcodes.Or);
        AddJumpFromTo(jump_op, Peek());
      }
      break;
      case EnumBinaryOp.BIT_AND:
        VisitChildren(ast);
        Emit(Opcodes.BitAnd);
      break;
      case EnumBinaryOp.BIT_OR:
        VisitChildren(ast);
        Emit(Opcodes.BitOr);
      break;
      case EnumBinaryOp.MOD:
        VisitChildren(ast);
        Emit(Opcodes.Mod);
      break;
      case EnumBinaryOp.ADD:
        VisitChildren(ast);
        Emit(Opcodes.Add);
      break;
      case EnumBinaryOp.SUB:
        VisitChildren(ast);
        Emit(Opcodes.Sub);
      break;
      case EnumBinaryOp.DIV:
        VisitChildren(ast);
        Emit(Opcodes.Div);
      break;
      case EnumBinaryOp.MUL:
        VisitChildren(ast);
        Emit(Opcodes.Mul);
      break;
      case EnumBinaryOp.EQ:
        VisitChildren(ast);
        Emit(Opcodes.Equal);
      break;
      case EnumBinaryOp.NQ:
        VisitChildren(ast);
        Emit(Opcodes.NotEqual);
      break;
      case EnumBinaryOp.LT:
        VisitChildren(ast);
        Emit(Opcodes.LT);
      break;
      case EnumBinaryOp.LTE:
        VisitChildren(ast);
        Emit(Opcodes.LTE);
      break;
      case EnumBinaryOp.GT:
        VisitChildren(ast);
        Emit(Opcodes.GT);
      break;
      case EnumBinaryOp.GTE:
        VisitChildren(ast);
        Emit(Opcodes.GTE);
      break;
      default:
        throw new Exception("Not supported binary type: " + ast.type);
    }
  }

  public override void DoVisit(AST_UnaryOpExp ast)
  {
    VisitChildren(ast);

    switch(ast.type)
    {
      case EnumUnaryOp.NOT:
        Emit(Opcodes.UnaryNot);
      break;
      case EnumUnaryOp.NEG:
        Emit(Opcodes.UnaryNeg);
      break;
      default:
        throw new Exception("Not supported unary type: " + ast.type);
    }
  }

  public byte MapToValType(string type)
  {
    if(type == "int" || type == "float")
      return Val.NUMBER;
    else if(type == "string")
      return Val.STRING;
    else if(type == "bool")
      return Val.BOOL;
    else
      return Val.OBJ;
  }

  public override void DoVisit(AST_VarDecl ast)
  {
    //checking of there are default args
    if(ast.is_func_arg && ast.children.Count > 0)
    {                  
      var func_decl = func_decls[func_decls.Count-1];
      var arg_op = Emit(Opcodes.DefArg, new int[] { (int)ast.symb_idx - func_decl.required_args_num, 0 /*patched later*/ });
      VisitChildren(ast);
      AddJumpFromTo(arg_op, Peek(), operand_idx: 1);
    }

    if(!ast.is_func_arg)
    {
      byte val_type = MapToValType(ast.type);
      Emit(Opcodes.DeclVar, new int[] { (int)ast.symb_idx, (int)val_type });
    }
    else
    {
      if(ast.is_ref)
        Emit(Opcodes.ArgRef, new int[] { (int)ast.symb_idx });
      else
        Emit(Opcodes.ArgVar, new int[] { (int)ast.symb_idx });
    }
  }

  public override void DoVisit(bhl.AST_JsonObj ast)
  {
    Emit(Opcodes.New, new int[] { AddConstant(ast.type) });
    VisitChildren(ast);
  }

  public override void DoVisit(bhl.AST_JsonArr ast)
  {
    var arr_symb = symbols.Resolve(ast.type) as ArrayTypeSymbol;
    if(arr_symb == null)
      throw new Exception("Could not find class binding: " + ast.type);

    Emit(Opcodes.New, new int[] { AddConstant(ast.type) });

    for(int i=0;i<ast.children.Count;++i)
    {
      var c = ast.children[i];

      //checking if there's an explicit add to array operand
      if(c is AST_JsonArrAddItem)
      {
        Emit(Opcodes.CallMethodNative, new int[] { GenericArrayTypeSymbol.IDX_AddInplace, AddConstant(ast.type), 0 });
      }
      else
        Visit(c);
    }

    //adding last item item
    if(ast.children.Count > 0)
    {
      Emit(Opcodes.CallMethodNative, new int[] { GenericArrayTypeSymbol.IDX_AddInplace, AddConstant(ast.type), 0 });
    }
  }

  public override void DoVisit(bhl.AST_JsonArrAddItem ast)
  {
    //NOTE: it's handled above
  }

  public override void DoVisit(bhl.AST_JsonPair ast)
  {
    VisitChildren(ast);
    Emit(Opcodes.SetAttrInplace, new int[] { AddConstant(ast.scope_type), (int)ast.symb_idx });
  }

#endregion
}

} //namespace bhl
