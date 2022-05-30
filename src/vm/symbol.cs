using System;
using System.Collections.Generic;

namespace bhl {

public abstract class Symbol : marshall.IMarshallableGeneric 
{
  public string name;
  public TypeProxy type;

  IType _type;
  public IType Type {
    get {
      if(_type == null)
        _type = type.Get();
      return _type;
    }
  }

  // All symbols know what scope contains them
  public IScope scope;
#if BHL_FRONT
  // Location in parse tree, can be null if it's a native symbol
  public WrappedParseTree parsed;
#endif

  public Symbol(string name, TypeProxy type) 
  { 
    this.name = name; 
    this.type = type;
  }

  public override string ToString() 
  {
    return name + '(' + type.name + ')';
  }

  public abstract uint ClassId();

  public virtual void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref name);
    marshall.Marshall.Sync(ctx, ref type);
  }
}

public abstract class BuiltInSymbol : Symbol, IType 
{
  public BuiltInSymbol(string name) 
    : base(name, default(TypeProxy)/*set below*/) 
  {
    this.type = new TypeProxy(this);
  }

  public string GetName() { return name; }

  //contains no data
  public override void Sync(marshall.SyncContext ctx)
  {
  }
}

public class IntSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 1;

  public IntSymbol()
    : base("int")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class BoolSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 2;

  public BoolSymbol()
    : base("bool")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class StringSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 3;

  public StringSymbol()
    : base("string")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class FloatSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 4;

  public FloatSymbol()
    : base("float")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class VoidSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 5;

  public VoidSymbol()
    : base("void")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class AnySymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 6;

  public AnySymbol()
    : base("any")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public class NullSymbol : BuiltInSymbol
{
  public const uint CLASS_ID = 7;

  public NullSymbol()
    : base("null")
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public abstract class InterfaceSymbol : EnclosingSymbol, IInstanceType
{
  public SymbolsStorage members;

  public SymbolsSet<InterfaceSymbol> inherits = new SymbolsSet<InterfaceSymbol>();

  HashSet<IInstanceType> related_types;

#if BHL_FRONT
  public InterfaceSymbol(
    WrappedParseTree parsed, 
    string name,
    IList<InterfaceSymbol> inherits = null
  )
    : this(name, inherits)
  {
    this.parsed = parsed;
  }
#endif

  public InterfaceSymbol(
    string name,
    IList<InterfaceSymbol> inherits = null
  )
    : base(name)
  {
    this.type = new TypeProxy(this);
    this.members = new SymbolsStorage(this);

    SetInherits(inherits);
  }

  //marshall factory version
  public InterfaceSymbol()
    : this(null, null)
  {}

  void SetInherits(IList<InterfaceSymbol> inherits)
  {
    if(inherits != null)
    {
      foreach(var ext in inherits)
      {
        this.inherits.Add(ext);

        //NOTE: at the moment for resolving simplcity we add 
        //      extension members right into the interface itself
        var ext_members = ext.GetMembers();
        for(int i=0;i<ext_members.Count;++i)
        {
          var mem = ext_members[i]; 
          base.Define(mem);
        }
      }
    }
  }

  public string GetName() { return name; }

  public override IScope GetFallbackScope()
  {
    return null;
  }

  public override void Define(Symbol sym)
  {
    if(!(sym is FuncSymbol))
      throw new Exception("Only function symbols supported");
    base.Define(sym);
  }

  public override SymbolsStorage GetMembers() { return members; }

  public FuncSymbol FindMethod(string name)
  {
    return Resolve(name) as FuncSymbol;
  }

  public HashSet<IInstanceType> GetAllRelatedTypesSet()
  {
    if(related_types == null)
    {
      related_types = new HashSet<IInstanceType>();
      related_types.Add(this);
      for(int i=0;i<inherits.Count;++i)
      {
        var ext = (IInstanceType)inherits[i];
        if(!related_types.Contains(ext))
          related_types.UnionWith(ext.GetAllRelatedTypesSet());
      }
    }
    return related_types;
  }
}

public class InterfaceSymbolScript : InterfaceSymbol
{
  public const uint CLASS_ID = 18;
  
  public InterfaceSymbolScript(
    string name,
    IList<InterfaceSymbol> inherits = null
  )
    : base(name, inherits)
  {}

#if BHL_FRONT
  public InterfaceSymbolScript(
    WrappedParseTree parsed, 
    string name,
    IList<InterfaceSymbol> inherits = null
  )
    : this(name, inherits)
  {
    this.parsed = parsed;
  }
#endif

  //marshall factory version
  public InterfaceSymbolScript() 
    : this(null)
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    base.Sync(ctx);

    marshall.Marshall.Sync(ctx, ref inherits); 
    marshall.Marshall.Sync(ctx, ref members); 
  }
}

public class InterfaceSymbolNative : InterfaceSymbol
{
  public InterfaceSymbolNative(
    string name, 
    IList<InterfaceSymbol> inherits,
    params FuncSymbol[] funcs
  )
    : base(name, inherits)
  {
    foreach(var func in funcs)
      Define(func);
  }

  public override uint ClassId()
  {
    throw new NotImplementedException();
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    throw new NotImplementedException();
  }
}

public abstract class ClassSymbol : EnclosingSymbol, IInstanceType
{
  public ClassSymbol super_class;

  public SymbolsSet<InterfaceSymbol> implements = new SymbolsSet<InterfaceSymbol>();

  //contains mapping of implemented interface method indices 
  //to actual class method indices:
  //  [IFoo][3,1,0]
  //  [IBar][2,0]
  public Dictionary<InterfaceSymbol, List<int>> vtable = new Dictionary<InterfaceSymbol, List<int>>();

  HashSet<IInstanceType> related_types;

  public SymbolsStorage members;

  public VM.ClassCreator creator;

#if BHL_FRONT
  public ClassSymbol(
    WrappedParseTree parsed, 
    string name, 
    ClassSymbol super_class, 
    IList<InterfaceSymbol> implements = null,
    VM.ClassCreator creator = null
  )
    : this(name, super_class, implements, creator)
  {
    this.parsed = parsed;
  }
#endif

  public ClassSymbol(
    string name, 
    ClassSymbol super_class, 
    IList<InterfaceSymbol> implements = null,
    VM.ClassCreator creator = null
  )
    : base(name)
  {
    this.members = new SymbolsStorage(this);
    this.type = new TypeProxy(this);
    this.creator = creator;

    SetSuperClass(super_class);
    SetImplements(implements);
  }

  void SetSuperClass(ClassSymbol super_class)
  {
    if(this.super_class == super_class)
      return;

    this.super_class = super_class;
    //NOTE: we define parent members in the current class
    //      scope as well. We do this since we want to  
    //      address its members simply by int index
    if(super_class != null)
    {
      var super_members = super_class.members;
      for(int i=0;i<super_members.Count;++i)
      {
        var mem = super_members[i];
        //NOTE: using base Define instead of our own version
        //      since we want to avoid 'already defined' checks
        base.Define(mem);
      }
    }
  }

  void SetImplements(IList<InterfaceSymbol> implements)
  {
    if(implements != null)
    {
      foreach(var imp in implements)
        this.implements.Add(imp);
    }
  }

  public void UpdateVTable()
  {
    vtable.Clear();

    var all = new HashSet<IInstanceType>();
    if(super_class != null)
    {
      for(int i=0;i<super_class.implements.Count;++i)
        all.UnionWith(super_class.implements[i].GetAllRelatedTypesSet());
    }
    for(int i=0;i<implements.Count;++i)
      all.UnionWith(implements[i].GetAllRelatedTypesSet());
    
    foreach(var imp in all)
    {
      var ifs2idx = new List<int>();
      var ifs = (InterfaceSymbol)imp;
      vtable.Add(ifs, ifs2idx);

      for(int midx=0;midx<ifs.members.Count;++midx)
      {
        var m = ifs.members[midx];
        var symb = Resolve(m.name) as FuncSymbol;
        if(symb == null)
          throw new Exception("No such method '" + m.name + "' in class '" + this.name + "'");
        ifs2idx.Add(symb.scope_idx);
      }
    }
  }

  public HashSet<IInstanceType> GetAllRelatedTypesSet()
  {
    if(related_types == null)
    {
      related_types = new HashSet<IInstanceType>();
      related_types.Add(this);
      if(super_class != null)
        related_types.UnionWith(super_class.GetAllRelatedTypesSet());
      for(int i=0;i<implements.Count;++i)
      {
        if(!related_types.Contains(implements[i]))
          related_types.UnionWith(implements[i].GetAllRelatedTypesSet());
      }
    }
    return related_types;
  }

  public override IScope GetFallbackScope() 
  {
    return super_class == null ? this.scope : super_class;
  }

  public override void Define(Symbol sym) 
  {
    if(super_class != null && super_class.members.Contains(sym.name))
      throw new SymbolError(sym, "already defined symbol '" + sym.name + "'"); 

    base.Define(sym);
  }

  public string GetName() { return name; }

  public override SymbolsStorage GetMembers() { return members; }
}

public abstract class ArrayTypeSymbol : ClassSymbol
{
  public readonly FuncSymbolNative FuncArrIdx = null;
  public readonly FuncSymbolNative FuncArrIdxW = null;

  public TypeProxy item_type;

  IType _item_type;
  public IType ItemType {
    get {
      if(_item_type == null)
        _item_type = item_type.Get();
      return _item_type;
    }
  }

  public ArrayTypeSymbol(string name, TypeProxy item_type)     
    : base(name, super_class: null)
  {
    this.item_type = item_type;

    this.creator = CreateArr;

    {
      var fn = new FuncSymbolNative("Add", Types.Void, Add,
        new FuncArgSymbol("o", item_type)
      );
      this.Define(fn);
    }

    {
      var fn = new FuncSymbolNative("RemoveAt", Types.Void, RemoveAt,
        new FuncArgSymbol("idx", Types.Int)
      );
      this.Define(fn);
    }

    {
      var fn = new FuncSymbolNative("Clear", Types.Void, Clear);
      this.Define(fn);
    }

    {
      var vs = new FieldSymbol("Count", Types.Int, GetCount, null);
      this.Define(vs);
    }

    {
      var fn = new FuncSymbolNative("IndexOf", Types.Int, IndexOf,
        new FuncArgSymbol("o", item_type)
      );
      this.Define(fn);
    }

    {
      //hidden system method not available directly
      FuncArrIdx = new FuncSymbolNative("$ArrIdx", item_type, ArrIdx);
    }

    {
      //hidden system method not available directly
      FuncArrIdxW = new FuncSymbolNative("$ArrIdxW", Types.Void, ArrIdxW);
    }
  }

  public ArrayTypeSymbol(TypeProxy item_type) 
    : this(item_type.name + "[]", item_type)
  {}

  public abstract void CreateArr(VM.Frame frame, ref Val v, IType type);
  public abstract void GetCount(VM.Frame frame, Val ctx, ref Val v, FieldSymbol fld);
  public abstract ICoroutine Add(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
  public abstract ICoroutine ArrIdx(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
  public abstract ICoroutine ArrIdxW(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
  public abstract ICoroutine RemoveAt(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
  public abstract ICoroutine IndexOf(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
  public abstract ICoroutine Clear(VM.Frame frame, FuncArgsInfo args_info, ref BHS status);
}

public class GenericArrayTypeSymbol : ArrayTypeSymbol
{
  public const uint CLASS_ID = 10; 

  public GenericArrayTypeSymbol(TypeProxy item_type) 
    : base(item_type)
  {
    name = "[]" + item_type.name;
  }

  //marshall factory version
  public GenericArrayTypeSymbol()
    : this(new TypeProxy())
  {}

  static IList<Val> AsList(Val arr)
  {
    var lst = arr.obj as IList<Val>;
    if(lst == null)
      throw new Exception("Not a ValList: " + (arr.obj != null ? arr.obj.GetType().Name : ""+arr));
    return lst;
  }

  public override void CreateArr(VM.Frame frm, ref Val v, IType type)
  {
    v.SetObj(ValList.New(frm.vm), type);
  }

  public override void GetCount(VM.Frame frm, Val ctx, ref Val v, FieldSymbol fld)
  {
    var lst = AsList(ctx);
    v.SetNum(lst.Count);
  }
  
  public override ICoroutine Add(VM.Frame frm, FuncArgsInfo args_info, ref BHS status)
  {
    var val = frm.stack.Pop();
    var arr = frm.stack.Pop();
    var lst = AsList(arr);
    lst.Add(val);
    val.Release();
    arr.Release();
    return null;
  }

  public override ICoroutine IndexOf(VM.Frame frm, FuncArgsInfo args_info, ref BHS status)
  {
    var val = frm.stack.Pop();
    var arr = frm.stack.Pop();
    var lst = AsList(arr);

    int idx = -1;
    for(int i=0;i<lst.Count;++i)
    {
      if(lst[i].IsValueEqual(val))
      {
        idx = i;
        break;
      }
    }

    val.Release();
    arr.Release();
    frm.stack.Push(Val.NewInt(frm.vm, idx));
    return null;
  }

  //NOTE: follows special Opcodes.ArrIdx conventions
  public override ICoroutine ArrIdx(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var lst = AsList(arr);
    var res = lst[idx]; 
    frame.stack.PushRetain(res);
    arr.Release();
    return null;
  }

  //NOTE: follows special Opcodes.ArrIdxW conventions
  public override ICoroutine ArrIdxW(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var val = frame.stack.Pop();
    var lst = AsList(arr);
    lst[idx] = val;
    val.Release();
    arr.Release();
    return null;
  }

  public override ICoroutine RemoveAt(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var lst = AsList(arr);
    lst.RemoveAt(idx); 
    arr.Release();
    return null;
  }

  public override ICoroutine Clear(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    var arr = frame.stack.Pop();
    var lst = AsList(arr);
    lst.Clear();
    arr.Release();
    return null;
  }

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref item_type);

    if(ctx.is_read)
      name = "[]" + item_type.name;
  }
}

public class ArrayTypeSymbolT<T> : ArrayTypeSymbol where T : new()
{
  public delegate IList<T> CreatorCb();
  public static CreatorCb Creator;

  public ArrayTypeSymbolT(string name, TypeProxy item_type, CreatorCb creator) 
    : base(name, item_type)
  {
    Creator = creator;
  }

  public ArrayTypeSymbolT(TypeProxy item_type, CreatorCb creator) 
    : base(item_type.name + "[]", item_type)
  {}

  public override void CreateArr(VM.Frame frm, ref Val v, IType type)
  {
    v.SetObj(Creator(), type);
  }

  public override void GetCount(VM.Frame frm, Val ctx, ref Val v, FieldSymbol fld)
  {
    v.SetNum(((IList<T>)ctx.obj).Count);
  }
  
  public override ICoroutine Add(VM.Frame frm, FuncArgsInfo args_info, ref BHS status)
  {
    var val = frm.stack.Pop();
    var arr = frm.stack.Pop();
    var lst = (IList<T>)arr.obj;
    lst.Add((T)val.obj);
    val.Release();
    arr.Release();
    return null;
  }

  public override ICoroutine IndexOf(VM.Frame frm, FuncArgsInfo args_info, ref BHS status)
  {
    var val = frm.stack.Pop();
    var arr = frm.stack.Pop();
    var lst = (IList<T>)arr.obj;
    int idx = lst.IndexOf((T)val.obj);
    val.Release();
    arr.Release();
    frm.stack.Push(Val.NewInt(frm.vm, idx));
    return null;
  }

  //NOTE: follows special Opcodes.ArrIdx conventions
  public override ICoroutine ArrIdx(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var lst = (IList<T>)arr.obj;
    var res = Val.NewObj(frame.vm, lst[idx], item_type.Get());
    frame.stack.Push(res);
    arr.Release();
    return null;
  }

  //NOTE: follows special Opcodes.ArrIdxW conventions
  public override ICoroutine ArrIdxW(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var val = frame.stack.Pop();
    var lst = (IList<T>)arr.obj;
    lst[idx] = (T)val.obj;
    val.Release();
    arr.Release();
    return null;
  }

  public override ICoroutine RemoveAt(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var lst = (IList<T>)arr.obj;
    lst.RemoveAt(idx); 
    arr.Release();
    return null;
  }

  public override ICoroutine Clear(VM.Frame frame, FuncArgsInfo args_info, ref BHS status)
  {
    int idx = (int)frame.stack.PopRelease().num;
    var arr = frame.stack.Pop();
    var lst = (IList<T>)arr.obj;
    lst.Clear();
    arr.Release();
    return null;
  }

  public override uint ClassId()
  {
    throw new NotImplementedException();
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    throw new NotImplementedException();
  }
}

public interface IScopeIndexed
{
  //index in an enclosing scope
  int scope_idx { get; set; }
}

public class VariableSymbol : Symbol, IScopeIndexed
{
  public const uint CLASS_ID = 8;

  int _scope_idx = -1;
  public int scope_idx {
    get {
      return _scope_idx;
    }
    set {
      _scope_idx = value;
    }
  }

#if BHL_FRONT
  //e.g. for  { { int a = 1 } } scope level will be 2
  public int scope_level;
  //once we leave the scope level the variable was defined at   
  //we mark this symbol as 'out of scope'
  public bool is_out_of_scope;

  public VariableSymbol(WrappedParseTree parsed, string name, TypeProxy type) 
    : this(name, type) 
  {
    this.parsed = parsed;
  }
#endif

  public VariableSymbol(string name, TypeProxy type) 
    : base(name, type) 
  {}

  //marshall factory version
  public VariableSymbol()
    : base("", new TypeProxy())
  {}

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    base.Sync(ctx);
    marshall.Marshall.Sync(ctx, ref _scope_idx);
  }
}

public class FuncArgSymbol : VariableSymbol
{
  public bool is_ref;

#if BHL_FRONT
  public FuncArgSymbol(WrappedParseTree parsed, string name, TypeProxy type, bool is_ref = false)
    : this(name, type, is_ref)
  {
    this.parsed = parsed;
  }
#endif

  public FuncArgSymbol(string name, TypeProxy type, bool is_ref = false)
    : base(name, type)
  {
    this.is_ref = is_ref;
  }
}

public class FieldSymbol : VariableSymbol
{
  public delegate void FieldGetter(VM.Frame frm, Val v, ref Val res, FieldSymbol fld);
  public delegate void FieldSetter(VM.Frame frm, ref Val v, Val nv, FieldSymbol fld);
  public delegate void FieldRef(VM.Frame frm, Val v, out Val res, FieldSymbol fld);

  public FieldGetter getter;
  public FieldSetter setter;
  public FieldRef getref;

  public FieldSymbol(string name, TypeProxy type, FieldGetter getter = null, FieldSetter setter = null, FieldRef getref = null) 
    : base(name, type)
  {
    this.getter = getter;
    this.setter = setter;
    this.getref = getref;
  }
}

public class FieldSymbolScript : FieldSymbol
{
  new public const uint CLASS_ID = 9;

  public FieldSymbolScript(string name, TypeProxy type) 
    : base(name, type, null, null, null)
  {
    this.getter = Getter;
    this.setter = Setter;
    this.getref = Getref;
  }

  //marshall factory version
  public FieldSymbolScript()
    : this("", new TypeProxy())
  {}

  void Getter(VM.Frame frm, Val ctx, ref Val v, FieldSymbol fld)
  {
    var m = (IList<Val>)ctx.obj;
    v.ValueCopyFrom(m[scope_idx]);
  }

  void Setter(VM.Frame frm, ref Val ctx, Val v, FieldSymbol fld)
  {
    var m = (IList<Val>)ctx.obj;
    var curr = m[scope_idx];
    for(int i=0;i<curr._refs;++i)
    {
      v.RefMod(RefOp.USR_INC);
      curr.RefMod(RefOp.USR_DEC);
    }
    curr.ValueCopyFrom(v);
  }

  void Getref(VM.Frame frm, Val ctx, out Val v, FieldSymbol fld)
  {
    var m = (IList<Val>)ctx.obj;
    v = m[scope_idx];
  }

  public override uint ClassId()
  {
    return CLASS_ID;
  }
}

public abstract class EnclosingSymbol : Symbol, IScope 
{
  abstract public SymbolsStorage GetMembers();

#if BHL_FRONT
  public EnclosingSymbol(WrappedParseTree parsed, string name) 
    : this(name)
  {
    this.parsed = parsed;
  }
#endif

  public EnclosingSymbol(string name) 
    : base(name, type: default(TypeProxy) /*using empty value*/)
  {}

  public virtual IScope GetFallbackScope() { return this.scope; }

  public virtual Symbol Resolve(string name) 
  {
    var s = GetMembers().Find(name);
    if(s != null)
    {
#if BHL_FRONT
      if(s is VariableSymbol vs && vs.is_out_of_scope)
        return null;
      else
#endif
        return s;
    }
    return null;
  }

  public virtual void Define(Symbol sym) 
  {
    var members = GetMembers(); 
    if(members.Contains(sym.name))
      throw new SymbolError(sym, "already defined symbol '" + sym.name + "'"); 

    if(sym is IScopeIndexed si && si.scope_idx == -1)
      si.scope_idx = members.Count; 

    members.Add(sym);
  }
}

public abstract class FuncSymbol : EnclosingSymbol, IScopeIndexed
{
  public FuncSignature signature;

  public SymbolsStorage members;

  int _scope_idx = -1;
  public int scope_idx {
    get {
      return _scope_idx;
    }
    set {
      _scope_idx = value;
    }
  }

#if BHL_FRONT
  public FuncSymbol(
    WrappedParseTree parsed, 
    string name, 
    FuncSignature sig,
    ClassSymbolScript class_scope = null
  ) 
    : this(name, sig, class_scope)
  {
    this.parsed = parsed;
  }
#endif

  public FuncSymbol(
    string name, 
    FuncSignature signature,
    ClassSymbolScript class_scope = null
  ) 
    : base(name)
  {
    this.members = new SymbolsStorage(this);
    this.signature = signature;
    this.type = new TypeProxy(signature);

    if(class_scope != null)
    {
      var this_symb = new FuncArgSymbol("this", new TypeProxy(class_scope));
      Define(this_symb);
    }
  }

  public override SymbolsStorage GetMembers() { return members; }

  public override IScope GetFallbackScope() 
  { 
    //NOTE: we forbid resolving class members 
    //      inside methods without 'this.' prefix on purpose
    if(scope is ClassSymbolScript cs)
      return cs.scope;
    else
      return this.scope; 
  }

  public IType GetReturnType()
  {
    return signature.ret_type.Get();
  }

  public SymbolsStorage GetArgs()
  {
    //let's skip hidden 'this' argument which is stored at 0 idx
    int this_offset = (scope is ClassSymbolScript) ? 1 : 0;

    var args = new SymbolsStorage(this);
    for(int i=0;i<signature.arg_types.Count;++i)
      args.Add((FuncArgSymbol)members[i + this_offset]);
    return args;
  }

  public int GetTotalArgsNum() { return signature.arg_types.Count; }
  public abstract int GetDefaultArgsNum();
  public int GetRequiredArgsNum() { return GetTotalArgsNum() - GetDefaultArgsNum(); } 

  public bool HasDefaultArgAt(int i)
  {
    if(i >= GetTotalArgsNum())
      return false;
    return i >= (GetDefaultArgsNum() - GetDefaultArgsNum());
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref name);
    marshall.Marshall.Sync(ctx, ref signature);
    if(ctx.is_read)
      type = new TypeProxy(signature);
    marshall.Marshall.Sync(ctx, ref _scope_idx);
  }

  public override string ToString()
  {
    string s = "";
    s += "func ";
    s += signature.ret_type.Get().GetName() + " ";
    s += name;
    s += "(";
    foreach(var arg in signature.arg_types)
      s += arg.name + ",";
    s = s.TrimEnd(',');
    s += ")";
    return s;
  }
}

public class FuncSymbolScript : FuncSymbol
{
  public const uint CLASS_ID = 13; 

  public int local_vars_num {
    get {
      return members.Count;
    }
  }

  public int default_args_num;
  public int ip_addr;

#if BHL_FRONT
  public FuncSymbolScript(
    WrappedParseTree parsed, 
    FuncSignature sig,
    string name,
    int default_args_num,
    int ip_addr,
    ClassSymbolScript class_scope = null
  ) 
    : base(name, sig, class_scope)
  {
    this.name = name;
    this.default_args_num = default_args_num;
    this.ip_addr = ip_addr;
    this.parsed = parsed;
  }
#endif

  //symbol factory version
  public FuncSymbolScript()
    : base(null, new FuncSignature())
  {}

  public override int GetDefaultArgsNum() { return default_args_num; }

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    base.Sync(ctx);

    //NOTE: we don't Sync actual arguments for now,
    //      we probably should be doint this however
    //      we need to separate arguments from local
    //      variables (check if they are stored in the 
    //      same members dictionary)

    marshall.Marshall.Sync(ctx, ref default_args_num);
    marshall.Marshall.Sync(ctx, ref ip_addr);
  }
}

#if BHL_FRONT
public class LambdaSymbol : FuncSymbolScript
{
  List<AST_UpVal> upvals;

  List<FuncSymbol> fdecl_stack;

  public LambdaSymbol(
    WrappedParseTree parsed, 
    string name,
    FuncSignature sig,
    List<AST_UpVal> upvals,
    List<FuncSymbol> fdecl_stack
  ) 
    : base(parsed, sig, name, 0, 0)
  {
    this.upvals = upvals;
    this.fdecl_stack = fdecl_stack;
  }

  public VariableSymbol AddUpValue(VariableSymbol src)
  {
    var local = new VariableSymbol(src.parsed, src.name, src.type);

    this.Define(local);

    var up = new AST_UpVal(local.name, local.scope_idx, src.scope_idx); 
    upvals.Add(up);

    return local;
  }

  public override Symbol Resolve(string name) 
  {
    var s = members.Find(name);
    if(s != null)
      return s;

    return ResolveUpvalue(name);
  }

  Symbol ResolveUpvalue(string name)
  {
    int my_idx = FindMyIdxInStack();
    if(my_idx == -1)
      throw new Exception("Not found");

    for(int i=my_idx;i-- > 0;)
    {
      var decl = fdecl_stack[i];

      //NOTE: only variable symbols are considered
      var res = decl.members.Find(name);
      if(res is VariableSymbol vs && !vs.is_out_of_scope)
        return AssignUpValues(vs, i+1, my_idx);
    }

    return null;
  }

  int FindMyIdxInStack()
  {
    for(int i=0;i<fdecl_stack.Count;++i)
    {
      if(fdecl_stack[i] == this)
        return i;
    }
    return -1;
  }

  VariableSymbol AssignUpValues(VariableSymbol vs, int from_idx, int to_idx)
  {
    VariableSymbol res = null;
    //now let's put this result into all nested lambda scopes 
    for(int j=from_idx;j<=to_idx;++j)
    {
      if(fdecl_stack[j] is LambdaSymbol lmb)
      {
        vs = lmb.AddUpValue(vs);
        if(res == null)
          res = vs;
      }
    }
    return res;
  }
}
#endif

public class FuncSymbolNative : FuncSymbol
{
  public delegate ICoroutine Cb(VM.Frame frm, FuncArgsInfo args_info, ref BHS status); 
  public Cb cb;

  int def_args_num;

  public FuncSymbolNative(
    string name, 
    TypeProxy ret_type, 
    Cb cb,
    params FuncArgSymbol[] args
  ) 
    : this(name, ret_type, 0, cb, args)
  {}

  public FuncSymbolNative(
    string name, 
    TypeProxy ret_type, 
    int def_args_num,
    Cb cb,
    params FuncArgSymbol[] args
  ) 
    : base(name, new FuncSignature(ret_type))
  {
    this.cb = cb;
    this.def_args_num = def_args_num;

    foreach(var arg in args)
    {
      base.Define(arg);
      signature.AddArg(arg.type);
    }
  }

  public override int GetDefaultArgsNum() { return def_args_num; }

  public override void Define(Symbol sym) 
  {
    throw new Exception("Defining symbols here is not allowed");
  }

  public override uint ClassId()
  {
    throw new NotImplementedException();
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    throw new NotImplementedException();
  }
}

public class ClassSymbolNative : ClassSymbol
{
  public ClassSymbolNative(string name, ClassSymbol super_class, VM.ClassCreator creator = null)
    : base(name, super_class, null, creator)
  {}

  public void OverloadBinaryOperator(FuncSymbol s)
  {
    if(s.GetTotalArgsNum() != 1)
      throw new SymbolError(s, "operator overload must have exactly one argument");

    if(s.GetReturnType() == Types.Void)
      throw new SymbolError(s, "operator overload return value can't be void");

    Define(s);
  }

  public override uint ClassId()
  {
    throw new NotImplementedException();
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    throw new NotImplementedException();
  }
}

public class ClassSymbolScript : ClassSymbol
{
  public const uint CLASS_ID = 11;

  public ClassSymbolScript(string name, ClassSymbol super_class = null, IList<InterfaceSymbol> implements = null)
    : base(name, super_class, implements)
  {
    this.creator = ClassCreator;
  }

#if BHL_FRONT
  public ClassSymbolScript(WrappedParseTree parsed, string name, ClassSymbol super_class = null, IList<InterfaceSymbol> implements = null)
    : this(name, super_class, implements)
  {
    this.parsed = parsed;
  }
#endif

  //marshall factory version
  public ClassSymbolScript() 
    : this(null, null, null)
  {}

  void ClassCreator(VM.Frame frm, ref Val data, IType type)
  {
    //TODO: add handling of native super class
    //if(super_class is ClassSymbolNative cn)
    //{
    //}

    //NOTE: object's raw data is a list
    var vl = ValList.New(frm.vm);
    data.SetObj(vl, type);
    
    for(int i=0;i<members.Count;++i)
    {
      var m = members[i];
      //NOTE: Members contain all kinds of symbols: methods and attributes,
      //      however we need to properly setup attributes only. 
      //      Methods will be initialized with special case 'nil' value.
      //      Maybe we should track data members and methods separately someday. 
      if(m is VariableSymbol)
      {
        var mtype = m.type.Get();
        var v = frm.vm.MakeDefaultVal(mtype);
        //adding directly, bypassing Retain call
        vl.lst.Add(v);
      }
      else
        vl.Add(frm.vm.Null);
    }
  }

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref name);

    string super_name = super_class == null ? "" : super_class.GetFullName();
    marshall.Marshall.Sync(ctx, ref super_name);
    if(ctx.is_read && super_name != "")
    {
      var rslv = ((SymbolFactory)ctx.factory).resolver;
      var tmp_class = (ClassSymbol)rslv.ResolveByFullName(super_name);
      if(tmp_class == null)
        throw new Exception("Parent class '" + super_name + "' not found");
      super_class = tmp_class;
    }

    //NOTE: this includes super class members as well, maybe we
    //      should make a copy which doesn't include parent members?
    marshall.Marshall.Sync(ctx, ref members);

    marshall.Marshall.Sync(ctx, ref implements); 

    if(ctx.is_read)
      UpdateVTable();
  }
}

public class EnumSymbol : EnclosingSymbol, IType
{
  public SymbolsStorage members;

#if BHL_FRONT
  public EnumSymbol(WrappedParseTree parsed, string name)
    : this(name)
  {
    this.parsed = parsed;
  }
#endif

  public EnumSymbol(string name)
     : base(name)
  {
    this.members = new SymbolsStorage(this);
    this.type = new TypeProxy(this);
  }

  public string GetName() { return name; }

  public override SymbolsStorage GetMembers() { return members; }

  public EnumItemSymbol FindValue(string name)
  {
    return base.Resolve(name) as EnumItemSymbol;
  }

  public override uint ClassId()
  {
    throw new NotImplementedException();
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    throw new NotImplementedException();
  }
}

public class EnumSymbolScript : EnumSymbol
{
  public const uint CLASS_ID = 12;

  public EnumSymbolScript(string name)
    : base(name)
  {}

  //marshall factory version
  public EnumSymbolScript()
    : base(null)
  {}

  //0 - OK, 1 - duplicate key, 2 - duplicate value
  public int TryAddItem(string name, int val)
  {
    for(int i=0;i<members.Count;++i)
    {
      var m = (EnumItemSymbol)members[i];
      if(m.val == val)
        return 2;
      else if(m.name == name)
        return 1;
    }

    var item = new EnumItemSymbol(name, val);
    //TODO: should be set by SymbolsDictionary
    item.scope = this;
    members.Add(item);
    return 0;
  }

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref name);
    marshall.Marshall.Sync(ctx, ref members);
    if(ctx.is_read)
    {
      for(int i=0;i<members.Count;++i)
      {
        var item = (EnumItemSymbol)members[i];
        item.scope = this;
        item.type = new TypeProxy(this); 
      }
    }
  }
}

public class EnumItemSymbol : Symbol, IType 
{
  public const uint CLASS_ID = 15;

  public EnumSymbol owner {
    get {
      return scope as EnumSymbol;
    }
  }
  public int val;

#if BHL_FRONT
  public EnumItemSymbol(WrappedParseTree parsed, string name, int val = 0) 
    : this(name, val) 
  {
    this.parsed = parsed;
  }
#endif

  public EnumItemSymbol(string name, int val = 0) 
    : base(name, new TypeProxy()) 
  {
    this.val = val;
  }

  //marshall factory version
  public EnumItemSymbol() 
    : base(null, new TypeProxy())
  {}

  //type name
  public string GetName() { return owner.name; }

  public override uint ClassId()
  {
    return CLASS_ID;
  }

  public override void Sync(marshall.SyncContext ctx)
  {
    marshall.Marshall.Sync(ctx, ref name);
    marshall.Marshall.Sync(ctx, ref val);
  }
}

public class SymbolsStorage : marshall.IMarshallable
{
  IScope scope;
  Dictionary<string, Symbol> str2symb = new Dictionary<string, Symbol>();
  List<Symbol> list = new List<Symbol>();

  public int Count
  {
    get {
      return list.Count;
    }
  }

  public Symbol this[int index]
  {
    get {
      return list[index];
    }
  }

  public SymbolsStorage(IScope scope)
  {
    this.scope = scope;
  }

  public bool Contains(string key)
  {
    return str2symb.ContainsKey(key);
  }

  public Symbol Find(string key)
  {
    Symbol s = null;
    str2symb.TryGetValue(key, out s);
    return s;
  }

  public void Add(Symbol s)
  {
    //if(s.scope != null && s.scope != scope)
    // throw new Exception("Symbol '" + s.name + "' scope is already set");
    if(s.scope == null)
      s.scope = scope;
    str2symb.Add(s.name, s);
    list.Add(s);
  }

  public void RemoveAt(int index)
  {
    var s = list[index];
    if(s.scope == scope)
      s.scope = null;
    str2symb.Remove(s.name);
    list.RemoveAt(index);
  }

  public Symbol TryAt(int index)
  {
    if(index < 0 || index >= list.Count)
      return null;
    return list[index];
  }

  public int IndexOf(Symbol s)
  {
    return list.IndexOf(s);
  }

  public int IndexOf(string key)
  {
    var s = Find(key);
    if(s == null)
      return -1;
    return IndexOf(s);
  }

  public void Clear()
  {
    foreach(var s in list)
    {
      if(s.scope == scope)
        s.scope = null;
    }
    str2symb.Clear();
    list.Clear();
  }

  public void Sync(marshall.SyncContext ctx) 
  {
    marshall.Marshall.SyncGeneric(ctx, list, delegate(marshall.IMarshallableGeneric tmp) {
        if(ctx.is_read)
        {
          //NOTE: we need to add new symbol to str2sym collection ASAP
          var s = (Symbol)tmp;
          s.scope = this.scope;
          str2symb.Add(s.name, s);
        }
    });
  }

  public void UnionWith(SymbolsStorage o)
  {
    for(int i=0;i<o.Count;++i)
      Add(o[i]);
  }

  public int FindStringKeyIndex(string key)
  {
    for(int i=0;i<list.Count;++i)
    {
      if(list[i].name == key)
        return i;
    }
    return -1;
  }
}

public class SymbolsSet<T> : marshall.IMarshallable where T : Symbol,IType
{
  List<string> names = new List<string>();
  List<T> list = new List<T>();

  public int Count
  {
    get {
      return list.Count;
    }
  }

  public T this[int index]
  {
    get {
      return list[index];
    }
  }

  public SymbolsSet()
  {}

  public bool Add(T s)
  {
    string name = s.GetFullName();
    if(names.IndexOf(name) != -1)
      return false;
    names.Add(name);
    list.Add(s);
    return true;
  }

  public void Clear()
  {
    names.Clear();
    list.Clear();
  }

  public void Sync(marshall.SyncContext ctx) 
  {
    marshall.Marshall.Sync(ctx, names); 

    if(ctx.is_read)
    {
      var rslv = ((SymbolFactory)ctx.factory).resolver;

      foreach(var name in names)
      {
        var symb = rslv.ResolveByFullName(name) as T;
        if(symb == null)
          throw new Exception("Symbol '" + name + "' not found");
        list.Add(symb);
      }
    }
  }
}

public class SymbolIndex 
{
  List<Symbol> index = new List<Symbol>();

  public int Add(Symbol sym)
  {
    int idx = index.IndexOf(sym);
    if(idx != -1)
      return idx;
    idx = index.Count;
    index.Add(sym);
    return idx;
  }

  public Symbol this[int i]
  {
    get {
      return index[i];
    }
  }

  public int IndexOf(Symbol s)
  {
    return index.IndexOf(s);
  }

  public SymbolIndex Clone()
  {
    var clone = new SymbolIndex();
    clone.index.AddRange(index);
    return clone;
  }
}

public class SymbolFactory : marshall.IFactory
{
  public Types types;
  public ISymbolResolver resolver;

  public SymbolFactory(Types types, ISymbolResolver resolver)
  {
    this.types = types;
    this.resolver = resolver;
  }

  public marshall.IMarshallableGeneric CreateById(uint id) 
  {
    switch(id)
    {
      case IntSymbol.CLASS_ID:
        return Types.Int;
      case FloatSymbol.CLASS_ID:
        return Types.Float;
      case StringSymbol.CLASS_ID:
        return Types.String;
      case BoolSymbol.CLASS_ID:
        return Types.Bool;
      case AnySymbol.CLASS_ID:
        return Types.Any;
      case VoidSymbol.CLASS_ID:
        return Types.Void;
      case VariableSymbol.CLASS_ID:
        return new VariableSymbol(); 
      case FieldSymbolScript.CLASS_ID:
        return new FieldSymbolScript(); 
      case GenericArrayTypeSymbol.CLASS_ID:
        return new GenericArrayTypeSymbol(); 
      case ClassSymbolScript.CLASS_ID:
        return new ClassSymbolScript();
      case InterfaceSymbolScript.CLASS_ID:
        return new InterfaceSymbolScript();
      case EnumSymbolScript.CLASS_ID:
        return new EnumSymbolScript();
      case EnumItemSymbol.CLASS_ID:
        return new EnumItemSymbol();
      case FuncSymbolScript.CLASS_ID:
        return new FuncSymbolScript();
      case FuncSignature.CLASS_ID:
        return new FuncSignature();
      case RefType.CLASS_ID:
        return new RefType();
      case TupleType.CLASS_ID:
        return new TupleType();
      case Namespace.CLASS_ID:
        return new Namespace();
      default:
        return null;
    }
  }
}

} //namespace bhl