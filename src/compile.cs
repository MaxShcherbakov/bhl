using System;
using System.IO;
using System.Collections.Generic;

namespace bhl {

public enum Opcodes
{
  Constant        = 1,
  Add             = 2,
  Sub             = 3,
  Div             = 4,
  Mul             = 5,
  SetVar          = 6,
  GetVar          = 7,
  FuncCall        = 8,
  SetMVar         = 9,
  GetMVar         = 10,
  MethodCall      = 11,
  Return          = 12,
  ReturnVal       = 13,
  Jump            = 14,
  CondJump        = 15,
  LoopJump        = 16,
  UnaryNot        = 17,
  UnaryNeg        = 18,
  And             = 19,
  Or              = 20,
  Mod             = 21,
  BitOr           = 22,
  BitAnd          = 23,
  Equal           = 24,
  NotEqual        = 25,
  Less            = 26,
  Greater         = 27,
  LessOrEqual     = 28,
  GreaterOrEqual  = 29,
  DefArg          = 31, //opcode for skipping func def args
  //TODO: make it an universal opcode
  TypeCastInt     = 32,
  TypeCastStr     = 33,
  ArrNew          = 34,
}

public enum SymbolScope
{
  Global = 1
}

public struct SymbolView
{
  public string name;
  public SymbolScope scope;
  public int index;
}

public class SymbolViewTable
{
  Dictionary<string, SymbolView> store = new Dictionary<string, SymbolView>();

  public SymbolView Define(string name)
  {
    var s = new SymbolView()
    {
      name = name,
      index = store.Count,
      scope  = SymbolScope.Global
    };

    if(!store.ContainsKey(name))
    {
      store[name] = s;
      return s;
    }
    else
    {
      return store[name];
    } 
  }

  public SymbolView Resolve(string name)
  {
    SymbolView s;
    if(!store.TryGetValue(name, out s))
     throw new Exception("No such symbol in table " + name);
    return s;
  }
}

public class Const
{
  public EnumLiteral type;
  public double num;
  public string str;

  public Const(AST_Literal lt)
  {
    type = lt.type;
    num = lt.nval;
    str = lt.sval;
  }

  public Val ToVal()
  {
    if(type == EnumLiteral.NUM)
      return Val.NewNum(num);
    else if(type == EnumLiteral.BOOL)
      return Val.NewBool(num == 1);
    else if(type == EnumLiteral.STR)
      return Val.NewStr(str);
    else if(type == EnumLiteral.NIL)
      return Val.NewNil();
    else
      throw new Exception("Bad type");
  }
}

public class Compiler : AST_Visitor
{
  class OpDefinition
  {
    public Opcodes name;
    public int[] operand_width; //each array item represents the size of the operand in bytes
  }

  BaseScope symbols;
  public BaseScope Symbols {
    get {
      return symbols;
    }
  }

  List<Const> constants = new List<Const>();
  public List<Const> Constants {
    get {
      return constants;
    }
  }

  List<Bytecode> scopes = new List<Bytecode>();

  List<SymbolViewTable> symbol_views = new List<SymbolViewTable>();

  Dictionary<string, uint> func2offset = new Dictionary<string, uint>();
  public Dictionary<string, uint> Func2Offset {
    get {
      return func2offset;
    }
  }
  
  Dictionary<byte, OpDefinition> opcode_decls = new Dictionary<byte, OpDefinition>();

  public Compiler(BaseScope symbols)
  {
    DeclareOpcodes();

    this.symbols = symbols;
    scopes.Add(new Bytecode());
    symbol_views.Add(new SymbolViewTable());
  }

  public void Compile(AST ast)
  {
    Visit(ast);
  }

  Bytecode GetCurrentScope()
  {
    return this.scopes[this.scopes.Count-1];
  }

  SymbolViewTable GetCurrentSymbolView()
  {
    return this.symbol_views[this.symbol_views.Count-1];
  }

  void EnterNewScope()
  {
    scopes.Add(new Bytecode());
    symbol_views.Add(new SymbolViewTable());
  }
 
  long LeaveCurrentScope()
  {
    var bytecode = scopes[0];

    long index = bytecode.Length;
    var curr_scope = GetCurrentScope();
    var curr_table = GetCurrentSymbolView();
    bytecode.Write(curr_scope);
    scopes.Remove(curr_scope);
    symbol_views.Remove(curr_table);

    return index;
  }

  int AddConstant(AST_Literal lt)
  {
    for(int i = 0 ; i < constants.Count; ++i)
    {
      var cn = constants[i];

      if(lt.type == cn.type && cn.num == lt.nval && cn.str == lt.sval)
        return i;
    }
    constants.Add(new Const(lt));
    return constants.Count-1;
  }

  void DeclareOpcodes()
  {
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Constant,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.And
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Or
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.BitAnd
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.BitOr
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Mod
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Add
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Sub
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Div
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Mul
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Equal
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.NotEqual
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Greater
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Less
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.GreaterOrEqual
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.LessOrEqual
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.UnaryNot
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.UnaryNeg
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.SetVar,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.GetVar,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.SetMVar,
        operand_width = new int[] { 4, 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.GetMVar,
        operand_width = new int[] { 4, 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.FuncCall,
        operand_width = new int[] { 4, 1 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.MethodCall,
        operand_width = new int[] { 4, 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Return
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.ReturnVal
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Jump,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.LoopJump,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.CondJump,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.DefArg,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.ArrNew
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.TypeCastInt,
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.TypeCastStr,
      }
    );
  }

  void DeclareOpcode(OpDefinition def)
  {
    opcode_decls.Add((byte)def.name, def);
  }

  OpDefinition LookupOpcode(Opcodes op)
  {
    OpDefinition def;
    if(!opcode_decls.TryGetValue((byte)op, out def))
       throw new Exception("No such opcode definition: " + op);
    return def;
  }

  public void ReplaceOpcode(int position, byte[] new_op_data)
  {
    ReplaceOpcode(position, new_op_data, GetCurrentScope());
  }

  public void ReplaceOpcode(int position, byte[] new_op_data, Bytecode scope)
  {
    var scope_bytes = scope.GetBytes();

    byte[] left_bytes = new byte[position];
    Array.Copy(scope_bytes, left_bytes, position);
    var opcode = (Opcodes)scope_bytes[position];
    
    var op_def = LookupOpcode(opcode);

    var replaced_op_length = 1;

    if(op_def.operand_width != null)
    {
      foreach(var operand in op_def.operand_width)
      {
        uint real_operand_width = (uint)position + 1;
        uint delta_width = real_operand_width;
        Bytecode.Decode(scope_bytes, ref real_operand_width);
        replaced_op_length += 1 + (int)(real_operand_width - delta_width);
      }
    }

    byte[] right_bytes = new byte[scope_bytes.Length - (position + replaced_op_length)];
    
    Array.Copy(scope_bytes, position + replaced_op_length, right_bytes, 0, right_bytes.Length);

    scope.Reset(scope_bytes,0);
    scope.Write(left_bytes, left_bytes.Length);
    scope.Write(new_op_data, new_op_data.Length);
    scope.Write(right_bytes, right_bytes.Length);
  }

  public void DeleteOpcode(int pointer)
  {
    ReplaceOpcode(pointer, new byte[]{});
  }

  public Compiler Emit(Opcodes op, int[] operands = null)
  {
    var curr_scope = GetCurrentScope();
    Emit(curr_scope, op, operands);
    return this;
  }

  void Emit(Bytecode buf, Opcodes op, int[] operands = null)
  {
    var def = LookupOpcode(op);

    buf.Write((byte)op);

    if(def.operand_width != null && (operands == null || operands.Length != def.operand_width.Length))
      throw new Exception("Invalid number of operands for opcode:" + op + ", expected:" + def.operand_width.Length);

    for(int i = 0; operands != null && i < operands.Length; ++i)
    {
      int width = def.operand_width[i];

      switch(width)
      {
        case 1:
          buf.Write((byte)operands[i]);
        break;
        case 2:
          buf.Write((ushort)operands[i]);
        break;
        case 4:
          buf.Write((uint)operands[i]);
        break;
        default:
          throw new Exception("Not supported operand width: " + width + " for opcode:" + op);
      }
    }
  }

  //NOTE: returns position of the opcode argument for 
  //      the jump index to be patched later
  int EmitConditionStatement(AST_Block ast)
  {
    Visit(ast.children[0]);
    Emit(Opcodes.CondJump, new int[] { 0 /*dummy placeholder*/});
    int pos = GetCurrentScope().Position;
    Visit(ast.children[1]);
    return pos;
  }

  void PatchJumpOffset(int jump_opcode_pos)
  {
    var curr_scope = GetCurrentScope();
    //TODO: this patching should happen inline 
    //      without getting and rewriting all bytes
    var bytes = curr_scope.GetBytes();
    int offset = curr_scope.Position - jump_opcode_pos;
    if(offset < 0 || offset >= 241) 
      throw new Exception("Invalid offset: " + offset);
    bytes[jump_opcode_pos - 1] = (byte)offset;
    curr_scope.Reset(bytes, 0);
    curr_scope.Write(bytes, bytes.Length);
  }

  public byte[] GetBytes()
  {
    return scopes[0].GetBytes();
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
  }

  public override void DoVisit(AST_FuncDecl ast)
  {
    EnterNewScope();
    VisitChildren(ast);
    Emit(Opcodes.Return);

    func2offset.Add(ast.name, (uint)LeaveCurrentScope());
  }

  public override void DoVisit(AST_LambdaDecl ast)
  {
  }

  public override void DoVisit(AST_ClassDecl ast)
  {
  }

  public override void DoVisit(AST_EnumDecl ast)
  {
  }

  public override void DoVisit(AST_Block ast)
  {
    switch(ast.type)
    {
      case EnumBlock.IF:
        switch(ast.children.Count)
        {
          case 2:
          {
            int pos = EmitConditionStatement(ast);
            PatchJumpOffset(pos);
          }
          break;
          case 3:
          {
            int pos = EmitConditionStatement(ast);
            Emit(Opcodes.Jump, new int[] { 0 /*dummy placeholder*/});
            PatchJumpOffset(pos);
            pos = GetCurrentScope().Position;
            Visit(ast.children[2]);
            PatchJumpOffset(pos);
          }
          break;
          default:
            throw new Exception("Not supported conditions count: " + ast.children.Count);
        }
      break;
      case EnumBlock.WHILE:
      {
        int block_ip = GetCurrentScope().Position;
        int pointer = EmitConditionStatement(ast);
        Emit(Opcodes.LoopJump, new int[] { GetCurrentScope().Position - block_ip
                                           + LookupOpcode(Opcodes.LoopJump).operand_width[0] });
        PatchJumpOffset(pointer);
      }
      break;
      default:
        //there will be behaviour node blocks
        VisitChildren(ast);
      break;
    }
  }

  public override void DoVisit(AST_TypeCast ast)
  {
    VisitChildren(ast);

    if(ast.ntype == SymbolTable._int.name.n)
      Emit(Opcodes.TypeCastInt);
    else if(ast.ntype == SymbolTable._string.name.n)
      Emit(Opcodes.TypeCastStr);
  }

  public override void DoVisit(AST_New ast)
  {
    switch(ast.type)
    {
      case "[]":
        Emit(Opcodes.ArrNew);
        VisitChildren(ast);
      break;
      default:
        throw new Exception("Not supported: " + ast.Name());
        //VisitChildren(ast);
    }
  }

  public override void DoVisit(AST_Inc ast)
  {
  }

  public override void DoVisit(AST_Call ast)
  {  
    SymbolView sv;
    switch(ast.type)
    {
      case EnumCall.VARW:
        sv = GetCurrentSymbolView().Define(ast.name);
        Emit(Opcodes.SetVar, new int[] { sv.index });
      break;
      case EnumCall.VAR:
        sv = GetCurrentSymbolView().Resolve(ast.name);
        Emit(Opcodes.GetVar, new int[] { sv.index });
      break;
      case EnumCall.FUNC:
        uint offset;
        func2offset.TryGetValue(ast.name, out offset);
        VisitChildren(ast);
        Emit(Opcodes.FuncCall, new int[] {(int)offset, (int)ast.cargs_bits});//figure out how cargs works
      break;
      case EnumCall.MVAR:
      {
        var class_symb = symbols.Resolve(ast.scope_ntype) as ClassSymbol;
        if(class_symb == null)
          throw new Exception("Class type not found: " + ast.scope_ntype);
        int memb_idx = class_symb.members.FindStringKeyIndex(ast.name);
        if(memb_idx == -1)
          throw new Exception("Member '" + ast.name + "' not found in class: " + ast.scope_ntype);

        VisitChildren(ast);

        //TODO: instead of scope_ntype it rather should be an index?
        Emit(Opcodes.GetMVar, new int[] { (int)ast.scope_ntype, memb_idx});
      }
      break;
      case EnumCall.MFUNC:
      {
        var class_symb = symbols.Resolve(ast.scope_ntype) as ClassSymbol;
        if(class_symb == null)
          throw new Exception("Class type not found: " + ast.scope_ntype);
        int memb_idx = class_symb.members.FindStringKeyIndex(ast.name);
        if(memb_idx == -1)
          throw new Exception("Member '" + ast.name + "' not found in class: " + ast.scope_ntype);

        VisitChildren(ast);
        //TODO: instead of scope_ntype it rather should be an index?
        Emit(Opcodes.MethodCall, new int[] {(int)ast.scope_ntype, memb_idx});
      }
      break;
      case EnumCall.ARR_IDX:
        Emit(Opcodes.MethodCall, new int[] { GenericArrayTypeSymbol.VM_Type, GenericArrayTypeSymbol.VM_AtIdx});
      break;
      case EnumCall.ARR_IDXW:
        Emit(Opcodes.MethodCall, new int[] { GenericArrayTypeSymbol.VM_Type, GenericArrayTypeSymbol.VM_SetIdx});
      break;
      default:
        throw new Exception("Not supported call: " + ast.type);
    }
  }

  public override void DoVisit(AST_Return node)
  {
    VisitChildren(node);
    Emit(Opcodes.ReturnVal);
  }

  public override void DoVisit(AST_Break node)
  {
  }

  public override void DoVisit(AST_PopValue node)
  {
  }

  public override void DoVisit(AST_Literal node)
  {
    Emit(Opcodes.Constant, new int[] { AddConstant(node) });
  }

  public override void DoVisit(AST_BinaryOpExp node)
  {
    VisitChildren(node);

    switch(node.type)
    {
      case EnumBinaryOp.AND:
        Emit(Opcodes.And);
      break;
      case EnumBinaryOp.OR:
        Emit(Opcodes.Or);
      break;
      case EnumBinaryOp.BIT_AND:
        Emit(Opcodes.BitAnd);
      break;
      case EnumBinaryOp.BIT_OR:
        Emit(Opcodes.BitOr);
      break;
      case EnumBinaryOp.MOD:
        Emit(Opcodes.Mod);
      break;
      case EnumBinaryOp.ADD:
        Emit(Opcodes.Add);
      break;
      case EnumBinaryOp.SUB:
        Emit(Opcodes.Sub);
      break;
      case EnumBinaryOp.DIV:
        Emit(Opcodes.Div);
      break;
      case EnumBinaryOp.MUL:
        Emit(Opcodes.Mul);
      break;
      case EnumBinaryOp.EQ:
        Emit(Opcodes.Equal);
      break;
      case EnumBinaryOp.NQ:
        Emit(Opcodes.NotEqual);
      break;
      case EnumBinaryOp.GT:
        Emit(Opcodes.Greater);
      break;
      case EnumBinaryOp.LT:
        Emit(Opcodes.Less);
      break;
      case EnumBinaryOp.GTE:
        Emit(Opcodes.GreaterOrEqual);
      break;
      case EnumBinaryOp.LTE:
        Emit(Opcodes.LessOrEqual);
      break;
      default:
        throw new Exception("Not supported binary type: " + node.type);
    }
  }

  public override void DoVisit(AST_UnaryOpExp node)
  {
    VisitChildren(node);

    switch(node.type)
    {
      case EnumUnaryOp.NOT:
        Emit(Opcodes.UnaryNot);
      break;
      case EnumUnaryOp.NEG:
        Emit(Opcodes.UnaryNeg);
      break;
      default:
        throw new Exception("Not supported unary type: " + node.type);
    }
  }

  public override void DoVisit(AST_VarDecl node)
  {
    if(node.children.Count > 0)
    {
      var index = 0;
      Emit(Opcodes.DefArg, new int[] { index });
      var pointer = GetCurrentScope().Position;
      VisitChildren(node);
      PatchJumpOffset(pointer);
    }

    SymbolView s = GetCurrentSymbolView().Define(node.name);
    Emit(Opcodes.SetVar, new int[] { s.index });
  }

  public override void DoVisit(bhl.AST_JsonObj node)
  {
  }

  public override void DoVisit(bhl.AST_JsonArr node)
  {
    throw new Exception("Not supported : " + node);
  }

  public override void DoVisit(bhl.AST_JsonPair node)
  {
  }

#endregion
}

public class Bytecode
{
  public ushort Position { get { return (ushort)stream.Position; } }
  public long Length { get { return stream.Length; } }

  MemoryStream stream = new MemoryStream();

  public Bytecode() {}

  public Bytecode(byte[] buffer)
  {
    stream.Write(buffer, 0, buffer.Length);
  }

  public void Reset(byte[] buffer, int size)
  {
    stream.SetLength(0);
    stream.Write(buffer, 0, size);
    stream.Position = 0;
  }

  public byte[] GetBytes()
  {
    //NOTE: documentation: "omits unused bytes"
    return stream.ToArray();
  }

  public static uint Decode(byte[] bytecode, ref uint ip)
  {
    int decoded = 0;

    ++ip;
    var A0 = bytecode[ip];

    if(A0 < 241)
    {
      decoded = A0;
    }
    else if(A0 > 240 && A0 < 248)
    {
      ++ip;
      var A1 = bytecode[ip];
      decoded = 240 + 256 * (A0 - 241) + A1;
    }
    else if(A0 == 249)
    { 
      ++ip;
      var A1 = bytecode[ip];
      ++ip;
      var A2 = bytecode[ip];

      decoded = 2288 + (256 * A1) + A2;
    }
    else if(A0 >= 250 && A0 <= 251)
    {
      //A1..A3/A4/A5/A6/A7/A8 as a from 3 to 8 -byte big-ending integer
      int len = 3 + A0 % 250;
      for(int i = 1; i <= len; ++i)
      {
        ++ip;
        decoded |= bytecode[ip] << (i-1) * 8; 
      }
    }
    else
      throw new Exception("Not supported code: " + A0);

    return (uint)decoded;
  }

  public void Write(byte value)
  {
    stream.WriteByte(value);
  }

  public void Write(sbyte value)
  {
    stream.WriteByte((byte)value);
  }

  // http://sqlite.org/src4/doc/trunk/www/varint.wiki
  public void Write(uint value)
  {
    if(value <= 65535)
    {
      Write((ushort)value);
      return;
    }
    if(value <= 67823)
    {
      Write((byte)249);
      Write((byte)((value - 2288) / 256));
      Write((byte)((value - 2288) % 256));
      return;
    }
    if(value <= 16777215)
    {
      Write((byte)250);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      return;
    }

    // all other values of uint
    Write((byte)251);
    Write((byte)(value & 0xFF));
    Write((byte)((value >> 8) & 0xFF));
    Write((byte)((value >> 16) & 0xFF));
    Write((byte)((value >> 24) & 0xFF));
  }

  //TODO: do we need to support that?
  //public void Write(ulong value)
  //{
  //  if(value <= 16777215)
  //  {
  //    Write((uint)value);
  //    return;
  //  }
  //  if(value <= 4294967295)
  //  {
  //    Write((byte)251);
  //    Write((byte)(value & 0xFF));
  //    Write((byte)((value >> 8) & 0xFF));
  //    Write((byte)((value >> 16) & 0xFF));
  //    Write((byte)((value >> 24) & 0xFF));
  //    return;
  //  }
  //  if(value <= 1099511627775)
  //  {
  //    Write((byte)252);
  //    Write((byte)(value & 0xFF));
  //    Write((byte)((value >> 8) & 0xFF));
  //    Write((byte)((value >> 16) & 0xFF));
  //    Write((byte)((value >> 24) & 0xFF));
  //    Write((byte)((value >> 32) & 0xFF));
  //    return;
  //  }
  //  if(value <= 281474976710655)
  //  {
  //    Write((byte)253);
  //    Write((byte)(value & 0xFF));
  //    Write((byte)((value >> 8) & 0xFF));
  //    Write((byte)((value >> 16) & 0xFF));
  //    Write((byte)((value >> 24) & 0xFF));
  //    Write((byte)((value >> 32) & 0xFF));
  //    Write((byte)((value >> 40) & 0xFF));
  //    return;
  //  }
  //  if(value <= 72057594037927935)
  //  {
  //    Write((byte)254);
  //    Write((byte)(value & 0xFF));
  //    Write((byte)((value >> 8) & 0xFF));
  //    Write((byte)((value >> 16) & 0xFF));
  //    Write((byte)((value >> 24) & 0xFF));
  //    Write((byte)((value >> 32) & 0xFF));
  //    Write((byte)((value >> 40) & 0xFF));
  //    Write((byte)((value >> 48) & 0xFF));
  //    return;
  //  }

  //  // all others
  //  {
  //    Write((byte)255);
  //    Write((byte)(value & 0xFF));
  //    Write((byte)((value >> 8) & 0xFF));
  //    Write((byte)((value >> 16) & 0xFF));
  //    Write((byte)((value >> 24) & 0xFF));
  //    Write((byte)((value >> 32) & 0xFF));
  //    Write((byte)((value >> 40) & 0xFF));
  //    Write((byte)((value >> 48) & 0xFF));
  //    Write((byte)((value >> 56) & 0xFF));
  //  }
  //}

  public void Write(char value)
  {
    Write((byte)value);
  }

  public void Write(short value)
  {
    Write((byte)(value & 0xff));
    Write((byte)((value >> 8) & 0xff));
  }

  public void Write(ushort value)
  {
    if(value <= 240)
    {
      Write((byte)value);
      return;
    }
    if(value <= 2287)
    {
      Write((byte)((value - 240) / 256 + 241));
      Write((byte)((value - 240) % 256));
      return;
    }

    {
      Write((byte)249);
      Write((byte)((value - 2288) / 256));
      Write((byte)((value - 2288) % 256));
      return;
    }
  }

  //NOTE: do we really need non-packed versions?
  //public void Write(int value)
  //{
  //  // little endian...
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //}

  //public void Write(uint value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //}

  //public void Write(long value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //  Write((byte)((value >> 32) & 0xff));
  //  Write((byte)((value >> 40) & 0xff));
  //  Write((byte)((value >> 48) & 0xff));
  //  Write((byte)((value >> 56) & 0xff));
  //}

  //public void Write(ulong value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //  Write((byte)((value >> 32) & 0xff));
  //  Write((byte)((value >> 40) & 0xff));
  //  Write((byte)((value >> 48) & 0xff));
  //  Write((byte)((value >> 56) & 0xff));
  //}

  public void Write(float value)
  {
    byte[] bytes = BitConverter.GetBytes(value);
    Write(bytes, bytes.Length);
  }

  public void Write(double value)
  {
    byte[] bytes = BitConverter.GetBytes(value);
    Write(bytes, bytes.Length);
  }

  public void Write(bool value)
  {
    stream.WriteByte((byte)(value ? 1 : 0));
  }

  public void Write(Bytecode buffer)
  {
    buffer.WriteTo(stream);
  }

  void WriteTo(MemoryStream buffer_stream)
  {
    stream.WriteTo(buffer_stream);
  }

  public void Write(byte[] buffer, int count)
  {
    stream.Write(buffer, 0, count);
  }

  public void Write(byte[] buffer, int offset, int count)
  {
    stream.Write(buffer, offset, count);
  }

  public void SeekZero()
  {
    stream.Seek(0, SeekOrigin.Begin);
  }

  public void StartMessage(short type)
  {
    SeekZero();

    // two bytes for size, will be filled out in FinishMessage
    Write((ushort)0);

    // two bytes for message type
    Write(type);
  }

  public void FinishMessage()
  {
    // jump to zero, replace size (short) in header, jump back
    long oldPosition = stream.Position;
    ushort sz = (ushort)(Position - (sizeof(ushort) * 2)); // length - header(short,short)

    SeekZero();
    Write(sz);
    stream.Position = oldPosition;
  }
}

} //namespace bhl
