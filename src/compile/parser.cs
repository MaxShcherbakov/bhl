using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace bhl {

public class ANTLR_Result
{
  public ITokenStream tokens { get; private set; }
  public bhlParser.ProgramContext prog { get; private set; }

  public ANTLR_Result(ITokenStream tokens, bhlParser.ProgramContext prog)
  {
    this.tokens = tokens;
    this.prog = prog;
  }
}

public class WrappedParseTree
{
  public IParseTree tree;
  public Module module;
  public ITokenStream tokens;
  public IType eval_type;
}

public class ANTLR_Parser : bhlBaseVisitor<object>
{
  public class Result
  {
    public Module module { get; private set; }
    public AST_Module ast { get; private set; }

    public Result(Module module, AST_Module ast)
    {
      this.module = module;
      this.ast = ast;
    }
  }

  Result result;

  ANTLR_Result parsed;

  static int lambda_id = 0;

  static int NextLambdaId()
  {
    Interlocked.Increment(ref lambda_id);
    return lambda_id;
  }

  Types types;

  //NOTE: In this mode only top symbols declarations are inspected.
  //      This mode is used when module is imported by another one.
  bool being_imported;

  Importer importer;

  Module module;

  Namespace ns;

  ITokenStream tokens;
  ParseTreeProperty<WrappedParseTree> tree_props = new ParseTreeProperty<WrappedParseTree>();

  class ParserPass
  {
    public IAST ast;
    public IScope scope;

    public bhlParser.VarDeclareAssignContext gvar_ctx;

    public bhlParser.FuncDeclContext func_ctx;
    public AST_FuncDecl func_ast;
    public FuncSymbolScript func_symb;

    public bhlParser.ClassDeclContext class_ctx;
    public ClassSymbolScript class_symb;

    public bhlParser.InterfaceDeclContext iface_ctx;
    public InterfaceSymbolScript iface_symb;

    public ParserPass(IAST ast, IScope scope, ParserRuleContext ctx)
    {
      this.ast = ast;
      this.scope = scope;
      this.gvar_ctx = ctx as bhlParser.VarDeclareAssignContext;
      this.func_ctx = ctx as bhlParser.FuncDeclContext;
      this.class_ctx = ctx as bhlParser.ClassDeclContext;
      this.iface_ctx = ctx as bhlParser.InterfaceDeclContext;
    }
  }

  List<ParserPass> passes = new List<ParserPass>();

  Stack<IScope> scopes = new Stack<IScope>();
  IScope curr_scope {
    get {
      return scopes.Peek();
    }
  }

  HashSet<FuncSymbol> return_found = new HashSet<FuncSymbol>();

  Dictionary<FuncSymbol, int> defers2func = new Dictionary<FuncSymbol, int>();
  int defer_stack {
    get {
      var fsymb = PeekFuncDecl();
      int v;
      defers2func.TryGetValue(fsymb, out v);
      return v;
    }

    set {
      var fsymb = PeekFuncDecl();
      defers2func[fsymb] = value;
    }
  }

  Dictionary<FuncSymbol, int> loops2func = new Dictionary<FuncSymbol, int>();
  int loops_stack {
    get {
      var fsymb = PeekFuncDecl();
      int v;
      loops2func.TryGetValue(fsymb, out v);
      return v;
    }

    set {
      var fsymb = PeekFuncDecl();
      loops2func[fsymb] = value;
    }
  }

  //NOTE: a list is used instead of stack, so that it's easier to traverse by index
  List<FuncSymbolScript> func_decl_stack = new List<FuncSymbolScript>();

  Stack<IType> json_type_stack = new Stack<IType>();

  Stack<bool> call_by_ref_stack = new Stack<bool>();

  Stack<AST_Tree> ast_stack = new Stack<AST_Tree>();

  public static CommonTokenStream Stream2Tokens(string file, Stream s)
  {
    var ais = new AntlrInputStream(s);
    var lex = new bhlLexer(ais);
    lex.AddErrorListener(new ErrorLexerListener(file));
    return new CommonTokenStream(lex);
  }

  public static Result ProcessFile(string file, Types ts, ANTLR_Parser.Importer imp)
  {
    using(var sfs = File.OpenRead(file))
    {
      var mod = new Module(ts, imp.FilePath2ModuleName(file), file);
      return ProcessStream(mod, sfs, ts, imp);
    }
  }

  public static bhlParser Stream2Parser(string file, Stream src)
  {
    var tokens = Stream2Tokens(file, src);
    var p = new bhlParser(tokens);
    p.AddErrorListener(new ErrorParserListener(file));
    p.ErrorHandler = new ErrorStrategy();
    return p;
  }
  
  public static Result ProcessStream(Module module, Stream src, Types ts, ANTLR_Parser.Importer imp = null, bool being_imported = false)
  {
    var p = Stream2Parser(module.file_path, src);
    var parsed = new ANTLR_Result(p.TokenStream, p.program());
    return ProcessParsed(module, parsed, ts, imp, being_imported);
  }

  public static Result ProcessParsed(Module module, ANTLR_Result parsed, Types ts, ANTLR_Parser.Importer imp = null, bool being_imported = false)
  {
    //var sw1 = System.Diagnostics.Stopwatch.StartNew();
    var f = new ANTLR_Parser(parsed, module, ts, imp, being_imported);
    var res = f.Process();
    //sw1.Stop();
    //Console.WriteLine("Module {0} ({1} sec)", module.norm_path, Math.Round(sw1.ElapsedMilliseconds/1000.0f,2));
    return res;
  }

  public interface IParsedCache
  {
    bool TryFetch(string file, out ANTLR_Result parsed);
  }

  public class Importer
  {
    List<string> include_path = new List<string>();
    Dictionary<string, Module> modules = new Dictionary<string, Module>(); 
    IParsedCache parsed_cache = null;

    public void SetParsedCache(IParsedCache cache)
    {
      parsed_cache = cache;
    }

    public void AddToIncludePath(string path)
    {
      include_path.Add(Util.NormalizeFilePath(path));
    }

    public List<string> GetIncludePath()
    {
      return include_path;
    }

    public Module TryGet(string path)
    {
      Module m = null;
      modules.TryGetValue(path, out m);
      return m;
    }

    public void Register(Module m)
    {
      modules.Add(m.file_path, m);
    }

    public Module ImportModule(Module curr_module, Types ts, string path)
    {
      string full_path;
      string norm_path;
      ResolvePath(curr_module.file_path, path, out full_path, out norm_path);

      //Console.WriteLine("IMPORT: " + full_path + " FROM:" + curr_module.file_path);

      //1. checking repeated imports
      if(curr_module.imports.ContainsKey(full_path))
      {
        //Console.WriteLine("HIT: " + full_path);
        return null;
      }

      //2. checking if already exists
      Module m = TryGet(full_path);
      if(m != null)
      {
        curr_module.imports.Add(full_path, m);
        return m;
      }

      //3. Ok, let's parse it otherwise
      m = new Module(ts, norm_path, full_path);
     
      //Console.WriteLine("ADDING: " + full_path + " TO:" + curr_module.file_path);
      curr_module.imports.Add(full_path, m);
      Register(m);

      ANTLR_Result parsed;
      //4. Let's try the parsed cache if it's present
      if(parsed_cache != null && parsed_cache.TryFetch(full_path, out parsed))
      {
        //Console.WriteLine("HIT " + full_path);
        ANTLR_Parser.ProcessParsed(m, parsed, ts, this, being_imported: true);
      }
      else
      {
        var stream = File.OpenRead(full_path);
        //Console.WriteLine("MISS " + full_path);
        ANTLR_Parser.ProcessStream(m, stream, ts, this, being_imported: true);
        stream.Close();
      }

      return m;
    }

    void ResolvePath(string self_path, string path, out string full_path, out string norm_path)
    {
      full_path = "";
      norm_path = "";

      if(path.Length == 0)
        throw new Exception("Bad path");

      full_path = Util.ResolveImportPath(include_path, self_path, path);
      norm_path = FilePath2ModuleName(full_path);
    }

    public string FilePath2ModuleName(string full_path)
    {
      full_path = Util.NormalizeFilePath(full_path);

      string norm_path = "";
      for(int i=0;i<include_path.Count;++i)
      {
        var inc_path = include_path[i];
        if(full_path.IndexOf(inc_path) == 0)
        {
          norm_path = full_path.Replace(inc_path, "");
          norm_path = norm_path.Replace('\\', '/');
          //stripping .bhl extension
          norm_path = norm_path.Substring(0, norm_path.Length-4);
          //stripping initial /
          norm_path = norm_path.TrimStart('/', '\\');
          break;
        }
      }

      if(norm_path.Length == 0)
        throw new Exception("File path '" + full_path + "' was not normalized");
      return norm_path;
    }
  }

  public ANTLR_Parser(ANTLR_Result parsed, Module module, Types types, Importer importer, bool being_imported = false)
  {
    this.parsed = parsed;
    this.tokens = parsed.tokens;

    this.types = types;
    this.module = module;

    ns = module.ns;
    ns.Link(types.ns);

    PushScope(ns);

    if(importer == null)
      importer = new Importer();
    this.importer = importer;

    this.being_imported = being_imported;
  }

  void FireError(IParseTree place, string msg) 
  {
    throw new SemanticError(module, place, tokens, msg);
  }

  void PushScope(IScope scope)
  {
    if(scope is FuncSymbolScript fsymb)
      func_decl_stack.Add(fsymb);
    scopes.Push(scope);
  }

  void PopScope()
  {
    if(curr_scope is FuncSymbolScript)
      func_decl_stack.RemoveAt(func_decl_stack.Count-1);
    scopes.Pop();
  }

  void PushAST(AST_Tree ast)
  {
    ast_stack.Push(ast);
  }

  void PopAST()
  {
    ast_stack.Pop();
  }

  void PopAddOptimizeAST()
  {
    var tmp = PeekAST();
    PopAST();
    if(tmp is AST_Interim intr)
    {
      if(intr.children.Count == 1)
        PeekAST().AddChild(intr.children[0]);
      else if(intr.children.Count > 1)
        PeekAST().AddChild(intr);
    }
    else
      PeekAST().AddChild(tmp);
  }

  void PopAddAST()
  {
    var tmp = PeekAST();
    PopAST();
    PeekAST().AddChild(tmp);
  }

  AST_Tree PeekAST()
  {
    return ast_stack.Peek();
  }

  WrappedParseTree Wrap(IParseTree t)
  {
    var w = tree_props.Get(t);
    if(w == null)
    {
      w = new WrappedParseTree();
      w.module = module;
      w.tree = t;
      w.tokens = tokens;

      tree_props.Put(t, w);
    }
    return w;
  }

  public Result Process()
  {
    if(result == null)
    {
      var root_ast = new AST_Module(module.name);
      PushAST(root_ast);
      VisitProgram(parsed.prog);
      PopAST();

      result = new Result(module, root_ast);
    }
    return result;
  }

  public override object VisitProgram(bhlParser.ProgramContext ctx)
  {
    for(int i=0;i<ctx.progblock().Length;++i)
      Visit(ctx.progblock()[i]);

    VisitPasses();

    return null;
  }

  public override object VisitProgblock(bhlParser.ProgblockContext ctx)
  {
    var imps = ctx.imports();
    if(imps != null)
      Visit(imps);
    
    Visit(ctx.decls()); 

    return null;
  }

  void AddPass(ParserRuleContext ctx, IScope scope, IAST ast)
  {
    passes.Add(new ParserPass(ast, scope, ctx));
  }

  void VisitPasses()
  {
    foreach(var pass in passes)
    {
      if(pass.iface_ctx != null)
      {
        Pass_OutlineInterfaceDecl(pass);
      }

      if(pass.class_ctx != null)
      {
        Pass_OutlineClassDecl(pass);
      }

      if(pass.func_ctx != null)
      {
        Pass_OutlineFuncDecl(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.class_ctx != null)
      {
        Pass_AddClassExtensions(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.iface_ctx != null)
      {
        Pass_FinalizeInterfaceMethods(pass);
      }

      if(pass.class_ctx != null)
      {
        Pass_SetClassMembersTypes(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.iface_ctx != null)
      {
        Pass_AddInterfaceExtensions(pass);
      }

      if(pass.func_ctx != null)
      {
        Pass_FinalizeFuncSignature(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.class_ctx != null)
      {
        Pass_FinalizeClass(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.gvar_ctx != null)
      {
        Pass_VisitGlobalVar(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.class_ctx != null)
      {
        Pass_VisitClassMethodsBlocks(pass);
      }
    }

    foreach(var pass in passes)
    {
      if(pass.func_ctx != null)
      {
        Pass_VisitFuncBlock(pass);
      }
    }
  }

  public override object VisitImports(bhlParser.ImportsContext ctx)
  {
    var ast = new AST_Import();

    var imps = ctx.mimport();
    for(int i=0;i<imps.Length;++i)
      AddImport(ast, imps[i]);

    PeekAST().AddChild(ast);
    return null;
  }

  public void AddImport(AST_Import ast, bhlParser.MimportContext ctx)
  {
    var name = ctx.NORMALSTRING().GetText();
    //removing quotes
    name = name.Substring(1, name.Length-2);
    
    var imported = importer.ImportModule(this.module, types, name);
    //NOTE: null means module is already imported
    if(imported != null)
    {
      ns.Link(imported.ns);
      ast.module_names.Add(imported.name);
    }
  }

  public override object VisitSymbCall(bhlParser.SymbCallContext ctx)
  {
    var exp = ctx.callExp(); 
    Visit(exp);
    var eval_type = Wrap(exp).eval_type;
    if(eval_type != null && eval_type != Types.Void)
    {
      var tuple = eval_type as TupleType;
      if(tuple != null)
      {
        for(int i=0;i<tuple.Count;++i)
          PeekAST().AddChild(new AST_PopValue());
      }
      else
        PeekAST().AddChild(new AST_PopValue());
    }
    return null;
  }

  public override object VisitCallExp(bhlParser.CallExpContext ctx)
  {
    IType curr_type = null;

    ProcChainedCall(ctx.DOT() != null ? ns : curr_scope, ctx.NAME(), ctx.chainExp(), ref curr_type, ctx.Start.Line, write: false);

    Wrap(ctx).eval_type = curr_type;
    return null;
  }

  public override object VisitLambdaCall(bhlParser.LambdaCallContext ctx)
  {
    CommonVisitLambda(ctx, ctx.funcLambda());
    return null;
  }

  void ProcChainedCall(
    IScope scope,
    ITerminalNode root_name, 
    bhlParser.ChainExpContext[] chain, 
    ref IType curr_type, 
    int line, 
    bool write
   )
  {
    AST_Interim pre_call = null;
    PushAST(new AST_Interim());

    ITerminalNode curr_name = root_name;

    int ns_offset = 0;

    if(root_name != null)
    {
      var name_symb = scope.ResolveWithFallback(curr_name.GetText());
      if(name_symb == null)
        FireError(root_name, "symbol not resolved");

      //let's figure out the namespace offset
      if(name_symb is Namespace ns && chain != null)
      {
        scope = ns;
        for(ns_offset=0;ns_offset<chain.Length;)
        {
          var ch = chain[ns_offset];
          var macc = ch.memberAccess();
          if(macc == null)
            FireError(ch, "bad chain call");
          name_symb = scope.ResolveWithFallback(macc.NAME().GetText());
          if(name_symb == null)
            FireError(macc.NAME(), "symbol not resolved");
           curr_name = macc.NAME(); 
          ++ns_offset;
          if(name_symb is Namespace name_ns)
            scope = name_ns;
          else
            break;
        }
      }

      if(name_symb.type.Get() == null)
        FireError(root_name, "bad chain call");
      curr_type = name_symb.type.Get();
    }

    int c = ns_offset;
    if(chain != null)
    {
      for(;c<chain.Length;++c)
      {
        var ch = chain[c];

        var cargs = ch.callArgs();
        var macc = ch.memberAccess();
        var arracc = ch.arrAccess();
        bool is_last = c == chain.Length-1;

        if(cargs != null)
        {
          ProcCallChainItem(scope, curr_name, cargs, null, ref curr_type, ref pre_call, line, write: false);
          curr_name = null;
        }
        else if(arracc != null)
        {
          ProcCallChainItem(scope, curr_name, null, arracc, ref curr_type, ref pre_call, line, write: write && is_last);
          curr_name = null;
        }
        else if(macc != null)
        {
          if(curr_name != null)
            ProcCallChainItem(scope, curr_name, null, null, ref curr_type, ref pre_call, line, write: false);

          scope = curr_type as IScope;
          if(!(scope is IInstanceType) && !(scope is EnumSymbol))
            FireError(macc, "type doesn't support member access via '.'");

          curr_name = macc.NAME();
        }
      }
    }

    //checking the leftover of the call chain
    if(curr_name != null)
      ProcCallChainItem(scope, curr_name, null, null, ref curr_type, ref pre_call, line, write);

    var chain_ast = PeekAST();
    PopAST();
    if(pre_call != null)
      PeekAST().AddChildren(pre_call);
    PeekAST().AddChildren(chain_ast);
  }

  void ProcCallChainItem(
    IScope scope, 
    ITerminalNode name, 
    bhlParser.CallArgsContext cargs, 
    bhlParser.ArrAccessContext arracc, 
    ref IType type, 
    ref AST_Interim pre_call,
    int line, 
    bool write
    )
  {
    AST_Call ast = null;

    if(name != null)
    {
      var name_symb = scope.ResolveWithFallback(name.GetText());
      if(name_symb == null)
        FireError(name, "symbol not resolved");

      var var_symb = name_symb as VariableSymbol;
      var func_symb = name_symb as FuncSymbol;
      var enum_symb = name_symb as EnumSymbol;
      var enum_item = name_symb as EnumItemSymbol;

      //func or method call
      if(cargs != null)
      {
        if(var_symb is FieldSymbol && !(var_symb.type.Get() is FuncSignature))
          FireError(name, "symbol is not a function");

        //func ptr
        if(var_symb != null && var_symb.type.Get() is FuncSignature)
        {
          var ftype = var_symb.type.Get() as FuncSignature;

          if(!(scope is IInstanceType))
          {
            ast = new AST_Call(EnumCall.FUNC_VAR, line, var_symb);
            AddCallArgs(ftype, cargs, ref ast, ref pre_call);
            type = ftype.ret_type.Get();
          }
          else //func ptr member of class
          {
            PeekAST().AddChild(new AST_Call(EnumCall.MVAR, line, var_symb));
            ast = new AST_Call(EnumCall.FUNC_MVAR, line, null);
            AddCallArgs(ftype, cargs, ref ast, ref pre_call);
            type = ftype.ret_type.Get();
          }
        }
        else if(func_symb != null)
        {
          ast = new AST_Call(scope is IInstanceType ? EnumCall.MFUNC : EnumCall.FUNC, line, func_symb);
          AddCallArgs(func_symb, cargs, ref ast, ref pre_call);
          type = func_symb.GetReturnType();
        }
        else
          FireError(name, "symbol is not a function");
      }
      //variable or attribute call
      else
      {
        if(var_symb != null)
        {
          bool is_write = write && arracc == null;
          bool is_global = var_symb.scope is Namespace;

          if(scope is InterfaceSymbol)
            FireError(name, "attributes not supported by interfaces");

          ast = new AST_Call(scope is IInstanceType ? 
            (is_write ? EnumCall.MVARW : EnumCall.MVAR) : 
            (is_global ? (is_write ? EnumCall.GVARW : EnumCall.GVAR) : (is_write ? EnumCall.VARW : EnumCall.VAR)), 
            line, 
            var_symb
          );
          //handling passing by ref for class fields
          if(scope is IInstanceType && PeekCallByRef())
          {
            if(scope is ClassSymbolNative)
              FireError(name, "getting field by 'ref' not supported for this class");
            ast.type = EnumCall.MVARREF; 
          }
          type = var_symb.type.Get();
        }
        else if(func_symb != null)
        {
          ast = new AST_Call(EnumCall.GET_ADDR, line, func_symb);
          type = func_symb.type.Get();
        }
        else if(enum_symb != null)
        {
          type = enum_symb;
        }
        else if(enum_item != null)
        {
          var ast_literal = new AST_Literal(ConstType.INT);
          ast_literal.nval = enum_item.val;
          PeekAST().AddChild(ast_literal);
        }
        else
          FireError(name, "symbol usage is not valid");
      }
    }
    else if(cargs != null)
    {
      var ftype = type as FuncSignature;
      if(ftype == null)
        FireError(cargs, "no func to call");
      
      ast = new AST_Call(EnumCall.LMBD, line, null);
      AddCallArgs(ftype, cargs, ref ast, ref pre_call);
      type = ftype.ret_type.Get();
    }

    if(ast != null)
      PeekAST().AddChild(ast);

    if(arracc != null)
      AddArrIndex(arracc, ref type, line, write);
  }

  void AddArrIndex(bhlParser.ArrAccessContext arracc, ref IType type, int line, bool write)
  {
    var arr_type = type as ArrayTypeSymbol;
    if(arr_type == null)
      FireError(arracc, "accessing not an array type '" + type.GetName() + "'");

    var arr_exp = arracc.exp();
    Visit(arr_exp);

    if(Wrap(arr_exp).eval_type != Types.Int)
      FireError(arr_exp, "array index expression is not of type int");

    type = arr_type.item_type.Get();

    var ast = new AST_Call(write ? EnumCall.ARR_IDXW : EnumCall.ARR_IDX, line, null);

    PeekAST().AddChild(ast);
  }

  class NormCallArg
  {
    public bhlParser.CallArgContext ca;
    public Symbol orig;
  }

  void AddCallArgs(FuncSymbol func_symb, bhlParser.CallArgsContext cargs, ref AST_Call call, ref AST_Interim pre_call)
  {     
    var func_args = func_symb.GetArgs();
    int total_args_num = func_symb.GetTotalArgsNum();
    //Console.WriteLine(func_args.Count + " " + total_args_num);
    int default_args_num = func_symb.GetDefaultArgsNum();
    int required_args_num = total_args_num - default_args_num;
    var args_info = new FuncArgsInfo();

    var norm_cargs = new List<NormCallArg>(total_args_num);
    for(int i=0;i<total_args_num;++i)
    {
      var arg = new NormCallArg();
      arg.orig = (Symbol)func_args[i];
      norm_cargs.Add(arg); 
    }

    //1. filling normalized call args
    for(int ci=0;ci<cargs.callArg().Length;++ci)
    {
      var ca = cargs.callArg()[ci];
      var ca_name = ca.NAME();

      var idx = ci;
      //NOTE: checking if it's a named arg and finding its index
      if(ca_name != null)
      {
        idx = func_args.IndexOf(ca_name.GetText());
        if(idx == -1)
          FireError(ca_name, "no such named argument");

        if(norm_cargs[idx].ca != null)
          FireError(ca_name, "argument already passed before");
      }
      
      if(idx >= func_args.Count)
        FireError(ca, "there is no argument " + (idx + 1) + ", total arguments " + func_args.Count);

      if(idx >= func_args.Count)
        FireError(ca, "too many arguments for function");

      norm_cargs[idx].ca = ca;
    }

    PushAST(call);
    IParseTree prev_ca = null;
    //2. traversing normalized args
    for(int i=0;i<norm_cargs.Count;++i)
    {
      var ca = norm_cargs[i].ca;

      //NOTE: if call arg is not specified, try to find the default one
      if(ca == null)
      {
        //this one is used for proper error reporting
        var next_arg = FindNextCallArg(cargs, prev_ca);

        if(i < required_args_num)
        {
          FireError(next_arg, "missing argument '" + norm_cargs[i].orig.name + "'");
        }
        else
        {
          //NOTE: for func native symbols we assume default arguments  
          //      are specified manually in bindings
          if(func_symb is FuncSymbolNative || 
            (func_symb is FuncSymbolScript fss && fss.HasDefaultArgAt(i)))
          {
            int default_arg_idx = i - required_args_num;
            if(!args_info.UseDefaultArg(default_arg_idx, true))
              FireError(next_arg, "max default arguments reached");
          }
          else
            FireError(next_arg, "missing argument '" + norm_cargs[i].orig.name + "'");
        }
      }
      else
      {
        prev_ca = ca;
        if(!args_info.IncArgsNum())
          FireError(ca, "max arguments reached");

        var func_arg_symb = (FuncArgSymbol)func_args[i];
        var func_arg_type = func_arg_symb.parsed == null ? func_arg_symb.type.Get() : func_arg_symb.parsed.eval_type;  

        bool is_ref = ca.isRef() != null;
        if(!is_ref && func_arg_symb.is_ref)
          FireError(ca, "'ref' is missing");
        else if(is_ref && !func_arg_symb.is_ref)
          FireError(ca, "argument is not a 'ref'");

        PushCallByRef(is_ref);
        PushJsonType(func_arg_type);
        PushAST(new AST_Interim());
        Visit(ca);
        TryProtectStackInterleaving(ca, func_arg_type, i, ref pre_call);
        PopAddOptimizeAST();
        PopJsonType();
        PopCallByRef();

        var wca = Wrap(ca);

        //NOTE: if symbol is from bindings we don't have a parse tree attached to it
        if(func_arg_symb.parsed == null)
        {
          if(func_arg_symb.type.Get() == null)
            FireError(ca, "invalid type");
          types.CheckAssign(func_arg_symb.type.Get(), wca);
        }
        else
          types.CheckAssign(func_arg_symb.parsed, wca);
      }
    }

    PopAST();

    call.cargs_bits = args_info.bits;
  }

  void AddCallArgs(FuncSignature func_type, bhlParser.CallArgsContext cargs, ref AST_Call call, ref AST_Interim pre_call)
  {     
    var func_args = func_type.arg_types;
    int ca_len = cargs.callArg().Length; 
    IParseTree prev_ca = null;
    PushAST(call);
    for(int i=0;i<func_args.Count;++i)
    {
      var arg_type_ref = func_args[i]; 

      if(i == ca_len)
      {
        var next_arg = FindNextCallArg(cargs, prev_ca);
        FireError(next_arg, "missing argument of type '" + arg_type_ref.name + "'");
      }

      var ca = cargs.callArg()[i];
      var ca_name = ca.NAME();

      if(ca_name != null)
        FireError(ca_name, "named arguments not supported for function pointers");

      var arg_type = arg_type_ref.Get();
      PushJsonType(arg_type);
      PushAST(new AST_Interim());
      Visit(ca);
      TryProtectStackInterleaving(ca, arg_type, i, ref pre_call);
      PopAddOptimizeAST();
      PopJsonType();

      var wca = Wrap(ca);
      types.CheckAssign(arg_type is RefType rt ? rt.subj.Get() : arg_type, wca);

      if(arg_type_ref.Get() is RefType && ca.isRef() == null)
        FireError(ca, "'ref' is missing");
      else if(!(arg_type_ref.Get() is RefType) && ca.isRef() != null)
        FireError(ca, "argument is not a 'ref'");

      prev_ca = ca;
    }
    PopAST();

    if(ca_len != func_args.Count)
      FireError(cargs, "too many arguments");

    var args_info = new FuncArgsInfo();
    if(!args_info.SetArgsNum(func_args.Count))
      FireError(cargs, "max arguments reached");
    call.cargs_bits = args_info.bits;
  }

  static bool HasFuncCalls(AST_Tree ast)
  {
    if(ast is AST_Call call && 
        (call.type == EnumCall.FUNC || 
         call.type == EnumCall.MFUNC ||
         call.type == EnumCall.FUNC_VAR ||
         call.type == EnumCall.FUNC_MVAR ||
         call.type == EnumCall.LMBD
         ))
      return true;
    
    for(int i=0;i<ast.children.Count;++i)
    {
      if(ast.children[i] is AST_Tree sub)
      {
        if(HasFuncCalls(sub))
          return true;
      }
    }
    return false;
  }

  //NOTE: We really want to avoid stack interleaving for the following case: 
  //        
  //        foo(1, bar())
  //      
  //      where bar() might execute for many ticks and at the same time 
  //      somewhere *in parallel* executes some another function which pushes 
  //      result onto the stack *before* bar() finishes its execution. 
  //
  //      At the time foo(..) is actually called the stack will contain badly 
  //      interleaved arguments! 
  //
  //      For this reason we rewrite the example above into something as follows:
  //
  //        tmp_1 = bar()
  //        foo(1, tmp_1)
  //
  //      We also should take into account cases like: 
  //
  //        foo(wow().bar())
  //
  //      At the same time we should not rewrite trivial cases like:
  //
  //        foo(bar())
  //
  //      Since in this case there is no stack interleaving possible (only one argument) 
  //      and we really want to avoid introduction of the new temp local variable
  void TryProtectStackInterleaving(bhlParser.CallArgContext ca, IType func_arg_type, int i, ref AST_Interim pre_call)
  {
    var arg_ast = PeekAST();
    if(i == 0 || !HasFuncCalls(arg_ast))
      return;

    PopAST();

    var var_tmp_symb = new VariableSymbol(Wrap(ca), "$_tmp_" + ca.Start.Line + "_" + ca.Start.Column, ns.T(func_arg_type));
    curr_scope.Define(var_tmp_symb);

    var var_tmp_decl = new AST_Call(EnumCall.VARW, ca.Start.Line, var_tmp_symb);
    var var_tmp_read = new AST_Call(EnumCall.VAR, ca.Start.Line, var_tmp_symb);

    if(pre_call == null)
      pre_call = new AST_Interim();
    foreach(var chain_child in arg_ast.children)
      pre_call.children.Add(chain_child);
    pre_call.children.Add(var_tmp_decl);

    PushAST(var_tmp_read);
  }

  IParseTree FindNextCallArg(bhlParser.CallArgsContext cargs, IParseTree curr)
  {
    for(int i=0;i<cargs.callArg().Length;++i)
    {
      var ch = cargs.callArg()[i];
      if(ch == curr && (i+1) < cargs.callArg().Length)
        return cargs.callArg()[i+1];
    }

    //NOTE: graceful fallback
    return cargs;
  }

  public override object VisitExpLambda(bhlParser.ExpLambdaContext ctx)
  {
    CommonVisitLambda(ctx, ctx.funcLambda());
    return null;
  }

  FuncSignature ParseFuncSignature(bhlParser.RetTypeContext ret_ctx, bhlParser.TypesContext types_ctx)
  {
    var ret_type = ParseType(ret_ctx);

    var arg_types = new List<TypeProxy>();
    if(types_ctx != null)
    {
      for(int i=0;i<types_ctx.refType().Length;++i)
      {
        var refType = types_ctx.refType()[i];
        var arg_type = ParseType(refType.type());
        if(refType.isRef() != null)
          arg_type = ns.TRef(arg_type);
        arg_types.Add(arg_type);
      }
    }

    return new FuncSignature(ret_type, arg_types);
  }

  FuncSignature ParseFuncSignature(bhlParser.FuncTypeContext ctx)
  {
    return ParseFuncSignature(ctx.retType(), ctx.types());
  }

  FuncSignature ParseFuncSignature(TypeProxy ret_type, bhlParser.FuncParamsContext fparams, out int default_args_num)
  {
    default_args_num = 0;
    var sig = new FuncSignature(ret_type);
    if(fparams != null)
    {
      for(int i=0;i<fparams.funcParamDeclare().Length;++i)
      {
        var vd = fparams.funcParamDeclare()[i];

        var tp = ParseType(vd.type());
        if(vd.isRef() != null)
          tp = ns.T(new RefType(tp));
        if(vd.assignExp() != null)
          ++default_args_num;
        sig.AddArg(tp);
      }
    }
    return sig;
  }

  FuncSignature ParseFuncSignature(TypeProxy ret_type, bhlParser.FuncParamsContext fparams)
  {
    int default_args_num;
    return ParseFuncSignature(ret_type, fparams, out default_args_num);
  }

  TypeProxy ParseType(bhlParser.RetTypeContext parsed)
  {
    TypeProxy tp;

    //convenience special case
    if(parsed == null)
      tp = Types.Void;
    else if(parsed.type().Length > 1)
    {
      var tuple = new TupleType();
      for(int i=0;i<parsed.type().Length;++i)
        tuple.Add(ParseType(parsed.type()[i]));
      tp = ns.T(tuple);
    }
    else
      tp = ParseType(parsed.type()[0]);

    if(tp.Get() == null)
      FireError(parsed, "type '" + tp.name + "' not found");

    return tp;
  }

  TypeProxy ParseType(bhlParser.TypeContext ctx)
  {
    TypeProxy tp;
    if(ctx.funcType() != null)
      tp = ns.T(ParseFuncSignature(ctx.funcType()));
    else
      tp = ns.T(ctx.nsName().GetText());

    if(ctx.ARR() != null)
      tp = ns.TArr(tp);

    if(tp.Get() == null)
      FireError(ctx, "type '" + tp.name + "' not found");

   return tp;
  }

  void CommonVisitLambda(IParseTree ctx, bhlParser.FuncLambdaContext funcLambda)
  {
    var tp = ParseType(funcLambda.retType());

    var func_name = Hash.CRC32(module.name) + "_lmb_" + NextLambdaId(); 
    var upvals = new List<AST_UpVal>();
    var lmb_symb = new LambdaSymbol(
      Wrap(ctx), 
      func_name,
      ParseFuncSignature(tp, funcLambda.funcParams()),
      upvals,
      this.func_decl_stack
    );

    var ast = new AST_LambdaDecl(lmb_symb, upvals, funcLambda.Stop.Line);

    var scope_backup = curr_scope;
    PushScope(lmb_symb);

    var fparams = funcLambda.funcParams();
    if(fparams != null)
    {
      PushAST(ast.fparams());
      Visit(fparams);
      PopAST();
    }

    //NOTE: all lambdas are defined in a global scope
    ns.Define(lmb_symb);
    //NOTE: ...however as a symbol resolve fallback we set the scope it's actually defined in 
    lmb_symb.scope = scope_backup;

    //NOTE: while we are inside lambda the eval type is its return type
    Wrap(ctx).eval_type = lmb_symb.GetReturnType();

    PushAST(ast.block());
    Visit(funcLambda.funcBlock());
    PopAST();

    if(tp.Get() != Types.Void && !return_found.Contains(lmb_symb))
      FireError(funcLambda.funcBlock(), "matching 'return' statement not found");

    //NOTE: once we are out of lambda the eval type is the lambda itself
    var curr_type = lmb_symb.type.Get(); 
    Wrap(ctx).eval_type = curr_type;

    PopScope();

    //NOTE: since lambda func symbol is currently compile-time only,
    //      we need to reflect local variables number in AST
    //      (for regular funcs this number is taken from a symbol)
    ast.local_vars_num = lmb_symb.local_vars_num;

    var chain = funcLambda.chainExp(); 
    if(chain != null)
    {
      var interim = new AST_Interim();
      interim.AddChild(ast);
      PushAST(interim);
      ProcChainedCall(curr_scope, null, chain, ref curr_type, funcLambda.Start.Line, write: false);
      PopAST();
      Wrap(ctx).eval_type = curr_type;
      PeekAST().AddChild(interim);
    }
    else
      PeekAST().AddChild(ast);

  }
  
  public override object VisitCallArg(bhlParser.CallArgContext ctx)
  {
    var exp = ctx.exp();
    Visit(exp);
    Wrap(ctx).eval_type = Wrap(exp).eval_type;
    return null;
  }

  public override object VisitExpJsonObj(bhlParser.ExpJsonObjContext ctx)
  {
    var json = ctx.jsonObject();

    Visit(json);
    Wrap(ctx).eval_type = Wrap(json).eval_type;
    return null;
  }

  public override object VisitExpJsonArr(bhlParser.ExpJsonArrContext ctx)
  {
    var json = ctx.jsonArray();
    Visit(json);
    Wrap(ctx).eval_type = Wrap(json).eval_type;
    return null;
  }

  public override object VisitJsonObject(bhlParser.JsonObjectContext ctx)
  {
    var new_exp = ctx.newExp();

    if(new_exp != null)
    {
      var tp = ParseType(new_exp.type());
      PushJsonType(tp.Get());
    }

    var curr_type = PeekJsonType();

    if(curr_type == null)
      FireError(ctx, "{..} not expected");

    if(!(curr_type is ClassSymbol) || (curr_type is ArrayTypeSymbol))
      FireError(ctx, "type '" + curr_type + "' can't be specified with {..}");

    Wrap(ctx).eval_type = curr_type;

    var ast = new AST_JsonObj(curr_type, ctx.Start.Line);

    PushAST(ast);
    var pairs = ctx.jsonPair();
    for(int i=0;i<pairs.Length;++i)
    {
      var pair = pairs[i]; 
      Visit(pair);
    }
    PopAST();

    if(new_exp != null)
      PopJsonType();

    PeekAST().AddChild(ast);
    return null;
  }

  public override object VisitJsonArray(bhlParser.JsonArrayContext ctx)
  {
    var curr_type = PeekJsonType();
    if(curr_type == null)
      FireError(ctx, "[..] not expected");

    if(!(curr_type is ArrayTypeSymbol))
      FireError(ctx, "[..] is not expected, need '" + curr_type + "'");

    var arr_type = curr_type as ArrayTypeSymbol;
    var orig_type = arr_type.item_type.Get();
    if(orig_type == null)
      FireError(ctx,  "type '" + arr_type.item_type.name + "' not found");
    PushJsonType(orig_type);

    var ast = new AST_JsonArr(arr_type, ctx.Start.Line);

    PushAST(ast);
    var vals = ctx.jsonValue();
    for(int i=0;i<vals.Length;++i)
    {
      Visit(vals[i]);
      //the last item is added implicitely
      if(i+1 < vals.Length)
        ast.AddChild(new AST_JsonArrAddItem());
    }
    PopAST();

    PopJsonType();

    Wrap(ctx).eval_type = arr_type;

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitJsonPair(bhlParser.JsonPairContext ctx)
  {
    var curr_type = PeekJsonType();
    var scoped_symb = curr_type as ClassSymbol;
    if(scoped_symb == null)
      FireError(ctx, "expecting class type, got '" + curr_type + "' instead");

    var name_str = ctx.NAME().GetText();
    
    var member = scoped_symb.ResolveWithFallback(name_str) as VariableSymbol;
    if(member == null)
      FireError(ctx, "no such attribute '" + name_str + "' in class '" + scoped_symb.name + "'");

    var ast = new AST_JsonPair(curr_type, name_str, member.scope_idx);

    PushJsonType(member.type.Get());

    var jval = ctx.jsonValue(); 
    PushAST(ast);
    Visit(jval);
    PopAST();

    PopJsonType();

    Wrap(ctx).eval_type = member.type.Get();

    PeekAST().AddChild(ast);
    return null;
  }

  public override object VisitJsonValue(bhlParser.JsonValueContext ctx)
  {
    var exp = ctx.exp();
    var jobj = ctx.jsonObject();
    var jarr = ctx.jsonArray();

    if(exp != null)
    {
      var curr_type = PeekJsonType();
      Visit(exp);
      Wrap(ctx).eval_type = Wrap(exp).eval_type;

      types.CheckAssign(curr_type, Wrap(exp));
    }
    else if(jobj != null)
      Visit(jobj);
    else
      Visit(jarr);

    return null;
  }

  public override object VisitExpTypeof(bhlParser.ExpTypeofContext ctx)
  {
    var tp = ParseType(ctx.@typeof().type());

    Wrap(ctx).eval_type = Types.ClassType;

    PeekAST().AddChild(new AST_Typeof(tp.Get()));

    return null;
  }

  public override object VisitExpCall(bhlParser.ExpCallContext ctx)
  {
    var exp = ctx.callExp(); 
    Visit(exp);
    Wrap(ctx).eval_type = Wrap(exp).eval_type;

    return null;
  }

  public override object VisitExpNew(bhlParser.ExpNewContext ctx)
  {
    var tp = ParseType(ctx.newExp().type());
    Wrap(ctx).eval_type = tp.Get();

    var ast = new AST_New(tp.Get(), ctx.Start.Line);
    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitAssignExp(bhlParser.AssignExpContext ctx)
  {
    var exp = ctx.exp();
    Visit(exp);
    Wrap(ctx).eval_type = Wrap(exp).eval_type;

    return null;
  }

  public override object VisitExpTypeCast(bhlParser.ExpTypeCastContext ctx)
  {
    var tp = ParseType(ctx.type());

    var ast = new AST_TypeCast(tp.Get(), ctx.Start.Line);
    var exp = ctx.exp();
    PushAST(ast);
    Visit(exp);
    PopAST();

    Wrap(ctx).eval_type = tp.Get();

    types.CheckCast(Wrap(ctx), Wrap(exp)); 

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpAs(bhlParser.ExpAsContext ctx)
  {
    var tp = ParseType(ctx.type());

    var ast = new AST_TypeAs(tp.Get(), ctx.Start.Line);
    var exp = ctx.exp();
    PushAST(ast);
    Visit(exp);
    PopAST();

    Wrap(ctx).eval_type = tp.Get();

    //TODO: do we need to pre-check absolutely unrelated types?
    //types.CheckCast(Wrap(ctx), Wrap(exp)); 

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpIs(bhlParser.ExpIsContext ctx)
  {
    var tp = ParseType(ctx.type());

    var ast = new AST_TypeIs(tp.Get(), ctx.Start.Line);
    var exp = ctx.exp();
    PushAST(ast);
    Visit(exp);
    PopAST();

    Wrap(ctx).eval_type = Types.Bool;

    //TODO: do we need to pre-check absolutely unrelated types?
    //types.CheckCast(Wrap(ctx), Wrap(exp)); 

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpUnary(bhlParser.ExpUnaryContext ctx)
  {
    EnumUnaryOp type;
    var op = ctx.operatorUnary().GetText(); 
    if(op == "-")
      type = EnumUnaryOp.NEG;
    else if(op == "!")
      type = EnumUnaryOp.NOT;
    else
      throw new Exception("Unknown type");

    var ast = new AST_UnaryOpExp(type);
    var exp = ctx.exp(); 
    PushAST(ast);
    Visit(exp);
    PopAST();

    Wrap(ctx).eval_type = type == EnumUnaryOp.NEG ? 
      types.CheckUnaryMinus(Wrap(exp)) : 
      types.CheckLogicalNot(Wrap(exp));

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpParen(bhlParser.ExpParenContext ctx)
  {
    var ast = new AST_Interim();
    var exp = ctx.exp(); 
    PushAST(ast);
    Visit(exp);

    var curr_type = Wrap(exp).eval_type;
    var chain = ctx.chainExp(); 
    if(chain != null)
      ProcChainedCall(curr_scope, null, chain, ref curr_type, exp.Start.Line, write: false);
    PopAST();
    
    PeekAST().AddChild(ast);
    
    Wrap(ctx).eval_type = curr_type;

    return null;
  }

  public override object VisitVarPostOpAssign(bhlParser.VarPostOpAssignContext ctx)
  {
    var lhs = ctx.NAME().GetText();
    var vlhs = curr_scope.ResolveWithFallback(lhs) as VariableSymbol;

    if(vlhs == null)
      FireError(ctx.NAME(), "symbol not resolved");

    if(!Types.IsRtlOpCompatible(vlhs.type.Get()))
      FireError(ctx.NAME(), "incompatible types");

    var op = $"{ctx.operatorPostOpAssign().GetText()[0]}";
    var op_type = GetBinaryOpType(op);
    AST_Tree bin_op_ast = new AST_BinaryOpExp(op_type, ctx.Start.Line);

    PushAST(bin_op_ast);
    bin_op_ast.AddChild(new AST_Call(EnumCall.VAR, ctx.Start.Line, vlhs));
    Visit(ctx.exp());
    PopAST();

    types.CheckAssign(vlhs.type.Get(), Wrap(ctx.exp()));

    PeekAST().AddChild(bin_op_ast);
    PeekAST().AddChild(new AST_Call(EnumCall.VARW, ctx.Start.Line, vlhs));

    return null;
  }

  public override object VisitExpAddSub(bhlParser.ExpAddSubContext ctx)
  {
    var op = ctx.operatorAddSub().GetText(); 

    CommonVisitBinOp(ctx, op, ctx.exp(0), ctx.exp(1));

    return null;
  }

  public override object VisitExpMulDivMod(bhlParser.ExpMulDivModContext ctx)
  {
    var op = ctx.operatorMulDivMod().GetText(); 

    CommonVisitBinOp(ctx, op, ctx.exp(0), ctx.exp(1));

    return null;
  }
  
  public override object VisitPostOperatorCall(bhlParser.PostOperatorCallContext ctx)
  {
    CommonVisitCallPostOperators(ctx.callPostOperators());
    return null;
  }

  void CommonVisitCallPostOperators(bhlParser.CallPostOperatorsContext ctx)
  {
    var v = ctx.NAME();
    var ast = new AST_Interim();
    
    var vs = curr_scope.ResolveWithFallback(v.GetText()) as VariableSymbol;
    if(vs == null)
      FireError(v, "symbol not resolved");
    
    bool is_negative = ctx.decrementOperator() != null;
    
    if(!Types.IsRtlOpCompatible(vs.type.Get())) // only numeric types
    {
      FireError(v,
        $"operator {(is_negative ? "--" : "++")} is not supported for {vs.type.name} type"
      );
    }
    
    if(is_negative)
      ast.AddChild(new AST_Dec(vs));
    else
      ast.AddChild(new AST_Inc(vs));
    
    Wrap(ctx).eval_type = Types.Void;
    PeekAST().AddChild(ast);
  }
  
  public override object VisitExpCompare(bhlParser.ExpCompareContext ctx)
  {
    var op = ctx.operatorComparison().GetText(); 

    CommonVisitBinOp(ctx, op, ctx.exp(0), ctx.exp(1));

    return null;
  }

  static EnumBinaryOp GetBinaryOpType(string op)
  {
    EnumBinaryOp op_type;

    if(op == "+")
      op_type = EnumBinaryOp.ADD;
    else if(op == "-")
      op_type = EnumBinaryOp.SUB;
    else if(op == "==")
      op_type = EnumBinaryOp.EQ;
    else if(op == "!=")
      op_type = EnumBinaryOp.NQ;
    else if(op == "*")
      op_type = EnumBinaryOp.MUL;
    else if(op == "/")
      op_type = EnumBinaryOp.DIV;
    else if(op == "%")
      op_type = EnumBinaryOp.MOD;
    else if(op == ">")
      op_type = EnumBinaryOp.GT;
    else if(op == ">=")
      op_type = EnumBinaryOp.GTE;
    else if(op == "<")
      op_type = EnumBinaryOp.LT;
    else if(op == "<=")
      op_type = EnumBinaryOp.LTE;
    else
      throw new Exception("Unknown type: " + op);

    return op_type;
  }

  void CommonVisitBinOp(ParserRuleContext ctx, string op, IParseTree lhs, IParseTree rhs)
  {
    EnumBinaryOp op_type = GetBinaryOpType(op);
    AST_Tree ast = new AST_BinaryOpExp(op_type, ctx.Start.Line);
    PushAST(ast);
    Visit(lhs);
    Visit(rhs);
    PopAST();

    var wlhs = Wrap(lhs);
    var wrhs = Wrap(rhs);

    var class_symb = wlhs.eval_type as ClassSymbol;
    //NOTE: checking if there's an operator overload
    if(class_symb != null && class_symb.Resolve(op) is FuncSymbol)
    {
      var op_func = class_symb.Resolve(op) as FuncSymbol;

      Wrap(ctx).eval_type = types.CheckBinOpOverload(ns, wlhs, wrhs, op_func);

      //NOTE: replacing original AST, a bit 'dirty' but kinda OK
      var over_ast = new AST_Interim();
      for(int i=0;i<ast.children.Count;++i)
        over_ast.AddChild(ast.children[i]);
      var op_call = new AST_Call(EnumCall.MFUNC, ctx.Start.Line, op_func, 1/*cargs bits*/);
      over_ast.AddChild(op_call);
      ast = over_ast;
    }
    else if(
      op_type == EnumBinaryOp.EQ || 
      op_type == EnumBinaryOp.NQ
    )
      Wrap(ctx).eval_type = types.CheckEqBinOp(wlhs, wrhs);
    else if(
      op_type == EnumBinaryOp.GT || 
      op_type == EnumBinaryOp.GTE ||
      op_type == EnumBinaryOp.LT || 
      op_type == EnumBinaryOp.LTE
    )
      Wrap(ctx).eval_type = types.CheckRtlBinOp(wlhs, wrhs);
    else
      Wrap(ctx).eval_type = types.CheckBinOp(wlhs, wrhs);

    PeekAST().AddChild(ast);
  }

  public override object VisitExpBitAnd(bhlParser.ExpBitAndContext ctx)
  {
    var ast = new AST_BinaryOpExp(EnumBinaryOp.BIT_AND, ctx.Start.Line);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    PushAST(ast);
    Visit(exp_0);
    Visit(exp_1);
    PopAST();

    Wrap(ctx).eval_type = types.CheckBitOp(Wrap(exp_0), Wrap(exp_1));

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpBitOr(bhlParser.ExpBitOrContext ctx)
  {
    var ast = new AST_BinaryOpExp(EnumBinaryOp.BIT_OR, ctx.Start.Line);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    PushAST(ast);
    Visit(exp_0);
    Visit(exp_1);
    PopAST();

    Wrap(ctx).eval_type = types.CheckBitOp(Wrap(exp_0), Wrap(exp_1));

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpAnd(bhlParser.ExpAndContext ctx)
  {
    var ast = new AST_BinaryOpExp(EnumBinaryOp.AND, ctx.Start.Line);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    //AND node has exactly two children
    var tmp0 = new AST_Interim();
    PushAST(tmp0);
    Visit(exp_0);
    PopAST();
    ast.AddChild(tmp0);

    var tmp1 = new AST_Interim();
    PushAST(tmp1);
    Visit(exp_1);
    PopAST();
    ast.AddChild(tmp1);

    Wrap(ctx).eval_type = types.CheckLogicalOp(Wrap(exp_0), Wrap(exp_1));

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpOr(bhlParser.ExpOrContext ctx)
  {
    var ast = new AST_BinaryOpExp(EnumBinaryOp.OR, ctx.Start.Line);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    //OR node has exactly two children
    var tmp0 = new AST_Interim();
    PushAST(tmp0);
    Visit(exp_0);
    PopAST();
    ast.AddChild(tmp0);

    var tmp1 = new AST_Interim();
    PushAST(tmp1);
    Visit(exp_1);
    PopAST();
    ast.AddChild(tmp1);

    Wrap(ctx).eval_type = types.CheckLogicalOp(Wrap(exp_0), Wrap(exp_1));

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpLiteralNum(bhlParser.ExpLiteralNumContext ctx)
  {
    AST_Literal ast = null;

    var number = ctx.number();
    var int_num = number.INT();
    var flt_num = number.FLOAT();
    var hex_num = number.HEX();

    if(int_num != null)
    {
      ast = new AST_Literal(ConstType.INT);
      Wrap(ctx).eval_type = Types.Int;
      ast.nval = double.Parse(int_num.GetText(), System.Globalization.CultureInfo.InvariantCulture);
    }
    else if(flt_num != null)
    {
      ast = new AST_Literal(ConstType.FLT);
      Wrap(ctx).eval_type = Types.Float;
      ast.nval = double.Parse(flt_num.GetText(), System.Globalization.CultureInfo.InvariantCulture);
    }
    else if(hex_num != null)
    {
      ast = new AST_Literal(ConstType.INT);
      Wrap(ctx).eval_type = Types.Int;
      ast.nval = Convert.ToUInt32(hex_num.GetText(), 16);
    }
    else
      FireError(ctx, "unknown numeric literal type");

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpLiteralFalse(bhlParser.ExpLiteralFalseContext ctx)
  {
    Wrap(ctx).eval_type = Types.Bool;

    var ast = new AST_Literal(ConstType.BOOL);
    ast.nval = 0;
    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpLiteralNull(bhlParser.ExpLiteralNullContext ctx)
  {
    Wrap(ctx).eval_type = Types.Null;

    var ast = new AST_Literal(ConstType.NIL);
    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpLiteralTrue(bhlParser.ExpLiteralTrueContext ctx)
  {
    Wrap(ctx).eval_type = Types.Bool;

    var ast = new AST_Literal(ConstType.BOOL);
    ast.nval = 1;
    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpLiteralStr(bhlParser.ExpLiteralStrContext ctx)
  {
    Wrap(ctx).eval_type = Types.String;

    var ast = new AST_Literal(ConstType.STR);
    ast.sval = ctx.@string().NORMALSTRING().GetText();
    //removing quotes
    ast.sval = ast.sval.Substring(1, ast.sval.Length-2);
    //adding convenience support for newlines and tabs
    ast.sval = ast.sval.Replace("\\n", "\n");
    ast.sval = ast.sval.Replace("\\\n", "\\n");
    ast.sval = ast.sval.Replace("\\t", "\t");
    ast.sval = ast.sval.Replace("\\\t", "\\t");
    PeekAST().AddChild(ast);

    return null;
  }

  FuncSymbolScript PeekFuncDecl()
  {
    if(func_decl_stack.Count == 0)
      return null;

    return func_decl_stack[func_decl_stack.Count-1];
  }
  void PushJsonType(IType type)
  {
    json_type_stack.Push(type);
  }

  void PopJsonType()
  {
    json_type_stack.Pop();
  }

  IType PeekJsonType()
  {
    if(json_type_stack.Count == 0)
      return null;

    return json_type_stack.Peek();
  }

  void PushCallByRef(bool flag)
  {
    call_by_ref_stack.Push(flag);
  }

  void PopCallByRef()
  {
    call_by_ref_stack.Pop();
  }

  bool PeekCallByRef()
  {
    if(call_by_ref_stack.Count == 0)
      return false;

    return call_by_ref_stack.Peek();
  }

  public override object VisitReturn(bhlParser.ReturnContext ctx)
  {
    if(defer_stack > 0)
      FireError(ctx, "return is not allowed in defer block");

    var func_symb = PeekFuncDecl();
    if(func_symb == null)
      FireError(ctx, "return statement is not in function");
    
    return_found.Add(func_symb);

    var ret_ast = new AST_Return(ctx.Start.Line);
    
    var explist = ctx.explist();
    if(explist != null)
    {
      int explen = explist.exp().Length;

      var fret_type = func_symb.GetReturnType();

      //NOTE: immediately adding return node in case of void return type
      if(fret_type == Types.Void)
        PeekAST().AddChild(ret_ast);
      else
        PushAST(ret_ast);

      var fmret_type = fret_type as TupleType;

      //NOTE: there can be a situation when explen == 1 but the return type 
      //      is actually a multi return, like in the following case:
      //
      //      func int,string bar() {
      //        return foo()
      //      }
      //
      //      where foo has the following signature: func int,string foo() {..}
      if(explen == 1)
      {
        var exp_item = explist.exp()[0];
        PushJsonType(fret_type);
        Visit(exp_item);
        PopJsonType();

        //NOTE: workaround for cases like: `return \n trace(...)`
        //      where exp has void type, in this case
        //      we simply ignore exp_node since return will take
        //      effect right before it
        if(Wrap(exp_item).eval_type != Types.Void)
          ret_ast.num = fmret_type != null ? fmret_type.Count : 1;

        types.CheckAssign(func_symb.parsed, Wrap(exp_item));
        Wrap(ctx).eval_type = Wrap(exp_item).eval_type;
      }
      else
      {
        if(fmret_type == null)
          FireError(ctx, "function doesn't support multi return");

        if(fmret_type.Count != explen)
          FireError(ctx, "multi return size doesn't match destination");

        var ret_type = new TupleType();

        //NOTE: we traverse expressions in reversed order so that returned
        //      values are properly placed on a stack
        for(int i=explen;i-- > 0;)
        {
          var exp = explist.exp()[i];
          Visit(exp);
          ret_type.Add(ns.T(Wrap(exp).eval_type));
        }

        //type checking is in proper order
        for(int i=0;i<explen;++i)
        {
          var exp = explist.exp()[i];
          types.CheckAssign(fmret_type[i].Get(), Wrap(exp));
        }

        Wrap(ctx).eval_type = ret_type;

        ret_ast.num = fmret_type.Count;
      }

      if(fret_type != Types.Void)
      {
        PopAST();
        PeekAST().AddChild(ret_ast);
      }
    }
    else
    {
      if(func_symb.GetReturnType() != Types.Void)
        FireError(ctx, "return value is missing");
      Wrap(ctx).eval_type = Types.Void;
      PeekAST().AddChild(ret_ast);
    }

    return null;
  }

  public override object VisitBreak(bhlParser.BreakContext ctx)
  {
    if(defer_stack > 0)
      FireError(ctx, "not within loop construct");

    if(loops_stack == 0)
      FireError(ctx, "not within loop construct");

    PeekAST().AddChild(new AST_Break());

    return null;
  }

  public override object VisitContinue(bhlParser.ContinueContext ctx)
  {
    if(defer_stack > 0)
      FireError(ctx, "not within loop construct");

    if(loops_stack == 0)
      FireError(ctx, "not within loop construct");

    PeekAST().AddChild(new AST_Continue());

    return null;
  }

  public override object VisitDecls(bhlParser.DeclsContext ctx)
  {
    var decls = ctx.decl();
    for(int i=0;i<decls.Length;++i)
    {
      var nsdecl = decls[i].nsDecl();
      if(nsdecl != null)
      {
        Visit(nsdecl);
        continue;
      }
      var vdecl = decls[i].varDeclareAssign();
      if(vdecl != null)
      {
        AddPass(vdecl, curr_scope, PeekAST()); 
        continue;
      }
      var fndecl = decls[i].funcDecl();
      if(fndecl != null)
      {
        AddPass(fndecl, curr_scope, PeekAST());
        continue;
      }
      var cldecl = decls[i].classDecl();
      if(cldecl != null)
      {
        AddPass(cldecl, curr_scope, PeekAST());
        continue;
      }
      var ifacedecl = decls[i].interfaceDecl();
      if(ifacedecl != null)
      {
        AddPass(ifacedecl, curr_scope, PeekAST());
        continue;
      }
      var edecl = decls[i].enumDecl();
      if(edecl != null)
      {
        Visit(edecl);
        continue;
      }
    }

    return null;
  }

  void Pass_OutlineFuncDecl(ParserPass pass)
  {
    string name = pass.func_ctx.NAME().GetText();

    pass.func_symb = new FuncSymbolScript(
      Wrap(pass.func_ctx), 
      new FuncSignature(),
      name
    );
    pass.scope.Define(pass.func_symb);

    pass.func_ast = new AST_FuncDecl(pass.func_symb, pass.func_ctx.Stop.Line);
    pass.ast.AddChild(pass.func_ast);
  }

  void Pass_FinalizeFuncSignature(ParserPass pass)
  {
    pass.func_ast.symbol.SetSignature(ParseFuncSignature(ParseType(pass.func_ctx.retType()), pass.func_ctx.funcParams()));

    VisitFuncParams(pass.func_ctx, pass.func_ast);

    Wrap(pass.func_ctx).eval_type = pass.func_ast.symbol.GetReturnType();
  }

  void VisitFuncParams(bhlParser.FuncDeclContext ctx, AST_FuncDecl func_ast)
  {
    var func_params = ctx.funcParams();
    if(func_params != null)
    {
      PushScope(func_ast.symbol);
      PushAST(func_ast.fparams());
      Visit(func_params);
      PopAST();
      PopScope();
    }

    func_ast.symbol.default_args_num = func_ast.GetDefaultArgsNum();
  }

  void Pass_VisitFuncBlock(ParserPass pass)
  {
    VisitFuncBlock(pass.func_ctx, pass.func_ast);
  }

  void VisitFuncBlock(bhlParser.FuncDeclContext ctx, AST_FuncDecl func_ast)
  {
    if(!being_imported)
    {
      PushScope(func_ast.symbol);

      PushAST(func_ast.block());
      Visit(ctx.funcBlock());
      PopAST();

      if(func_ast.symbol.GetReturnType() != Types.Void && !return_found.Contains(func_ast.symbol))
        FireError(ctx.NAME(), "matching 'return' statement not found");

      PopScope();
    }
  }

  void Pass_OutlineInterfaceDecl(ParserPass pass)
  {
    var name = pass.iface_ctx.NAME().GetText();

    pass.iface_symb = new InterfaceSymbolScript(Wrap(pass.iface_ctx), name, null);

    pass.scope.Define(pass.iface_symb);
  }

  void Pass_FinalizeInterfaceMethods(ParserPass pass)
  {
    PushScope(pass.scope);

    for(int i=0;i<pass.iface_ctx.interfaceBlock().interfaceMembers().interfaceMember().Length;++i)
    {
      var ib = pass.iface_ctx.interfaceBlock().interfaceMembers().interfaceMember()[i];

      var fd = ib.interfaceFuncDecl();
      if(fd != null)
      {
        int default_args_num;
        var sig = ParseFuncSignature(ParseType(fd.retType()), fd.funcParams(), out default_args_num);
        if(default_args_num != 0)
          FireError(ib, "default value is not allowed in this context");

        var func_symb = new FuncSymbolScript(null, sig, fd.NAME().GetText(), 0, 0);
        pass.iface_symb.Define(func_symb);

        var func_params = fd.funcParams();
        if(func_params != null)
        {
          PushScope(func_symb);
          //NOTE: we push some dummy interim AST and later
          //      simply discard it since we don't care about
          //      func args related AST for interfaces
          PushAST(new AST_Interim());
          Visit(func_params);
          PopAST();
          PopScope();
        }
      }
    }

    PopScope();
  }

  void Pass_AddInterfaceExtensions(ParserPass pass)
  {
    if(pass.iface_ctx.extensions() != null)
    {
      var inherits = new List<InterfaceSymbol>();
      for(int i=0;i<pass.iface_ctx.extensions().nsName().Length;++i)
      {
        var ext_name = pass.iface_ctx.extensions().nsName()[i]; 
        string ext_full_name = curr_scope.GetFullName(ext_name.GetText());
        var ext = ns.ResolveSymbolByFullName(ext_full_name);
        if(ext is InterfaceSymbol ifs)
        {
          if(ext == pass.iface_symb)
            FireError(ext_name, "self inheritance is not allowed");

          if(inherits.IndexOf(ifs) != -1)
            FireError(ext_name, "interface is inherited already");
          inherits.Add(ifs);
        }
        else
          FireError(ext_name, "not a valid interface");
      }
      if(inherits.Count > 0)
        pass.iface_symb.SetInherits(inherits);
    }
  }

  public override object VisitNsDecl(bhlParser.NsDeclContext ctx)
  {
    var name = ctx.NAME().GetText();

    var ns = curr_scope.Resolve(name) as Namespace;
    if(ns == null)
    {
      ns = new Namespace(types.gindex, name, module.name);
      curr_scope.Define(ns);
    }
    else if(ns.module_name != module.name)
      throw new Exception("Unexpected namespace's module name: " + ns.module_name);

    PushScope(ns);

    VisitDecls(ctx.decls());

    PopScope();

    return null;
  }

  void Pass_OutlineClassDecl(ParserPass pass)
  {
    var name = pass.class_ctx.NAME().GetText();

    pass.class_symb = new ClassSymbolScript(Wrap(pass.class_ctx), name, null, null);
    //class members
    for(int i=0;i<pass.class_ctx.classBlock().classMembers().classMember().Length;++i)
    {
      var cm = pass.class_ctx.classBlock().classMembers().classMember()[i];
      var vd = cm.varDeclare();
      if(vd != null)
      {
        if(vd.NAME().GetText() == "this")
          FireError(vd.NAME(), "the keyword \"this\" is reserved");

        var fld_symb = new FieldSymbolScript(vd.NAME().GetText(), new TypeProxy());
        pass.class_symb.tmp_members.Add(fld_symb);
      }

      var fd = cm.funcDecl();
      if(fd != null)
      {
        if(fd.NAME().GetText() == "this")
          FireError(fd.NAME(), "the keyword \"this\" is reserved");

        var func_symb = new FuncSymbolScript(
          Wrap(fd), 
          new FuncSignature(),
          fd.NAME().GetText()
        );
        func_symb.ReserveThisArgument(pass.class_symb);
        pass.class_symb.tmp_members.Add(func_symb);
      }
    }

    pass.scope.Define(pass.class_symb);
  }

  void Pass_SetClassMembersTypes(ParserPass pass)
  {
    PushScope(pass.scope);

    //class members
    for(int i=0;i<pass.class_ctx.classBlock().classMembers().classMember().Length;++i)
    {
      var cm = pass.class_ctx.classBlock().classMembers().classMember()[i];
      var vd = cm.varDeclare();
      if(vd != null)
      {
        var fld_symb = (FieldSymbolScript)pass.class_symb.tmp_members.Find(vd.NAME().GetText());
        fld_symb.type = ParseType(vd.type());
      }

      var fd = cm.funcDecl();
      if(fd != null)
      {
        var func_symb = (FuncSymbolScript)pass.class_symb.tmp_members.Find(fd.NAME().GetText());
        func_symb.SetSignature(ParseFuncSignature(ParseType(fd.retType()), fd.funcParams()));
        Wrap(fd).eval_type = func_symb.GetReturnType(); 
      }
    }

    PopScope();
  }

  void Pass_AddClassExtensions(ParserPass pass)
  {
    if(pass.class_ctx.extensions() != null)
    {
      var implements = new List<InterfaceSymbol>();
      ClassSymbol super_class = null;

      for(int i=0;i<pass.class_ctx.extensions().nsName().Length;++i)
      {
        var ext_name = pass.class_ctx.extensions().nsName()[i]; 
        string ext_full_name = curr_scope.GetFullName(ext_name.GetText());
        var ext = ns.ResolveSymbolByFullName(ext_full_name);
        if(ext is ClassSymbol cs)
        {
          if(ext == pass.class_symb)
            FireError(ext_name, "self inheritance is not allowed");

          if(super_class != null)
            FireError(ext_name, "only one parent class is allowed");

          if(cs is ClassSymbolNative)
            FireError(ext_name, "extending native classes is not supported");

          super_class = cs;
        }
        else if(ext is InterfaceSymbol ifs)
        {
          if(implements.IndexOf(ifs) != -1)
            FireError(ext_name, "interface is implemented already");
          implements.Add(ifs);
        }
        else
          FireError(ext_name, "not a class or an interface");
      }

      pass.class_symb.tmp_super_class = super_class;

      if(implements.Count > 0)
        pass.class_symb.SetImplementedInterfaces(implements);
    }
  }

  void Pass_FinalizeClass(ParserPass pass)
  {
    pass.class_symb.FinalizeClass();

    for(int i=0;i<pass.class_symb.implements.Count;++i)
      ValidateInterfaceImplementation(pass.class_ctx, pass.class_symb.implements[i], pass.class_symb);

    pass.class_symb.UpdateVTable();

  }

  void Pass_VisitClassMethodsBlocks(ParserPass pass)
  {
    PushScope(pass.scope);

    var ast_class = new AST_ClassDecl(pass.class_symb);

    //class methods bodies
    for(int i=0;i<pass.class_ctx.classBlock().classMembers().classMember().Length;++i)
    {
      var cm = pass.class_ctx.classBlock().classMembers().classMember()[i];
      var fd = cm.funcDecl();
      if(fd != null)
      {
        var func_symb = (FuncSymbolScript)pass.class_symb.Resolve(fd.NAME().GetText());

        var func_ast = new AST_FuncDecl(func_symb, fd.Stop.Line);

        VisitFuncParams(fd, func_ast);
        VisitFuncBlock(fd, func_ast);

        ast_class.func_decls.Add(func_ast);
      }
    }

    pass.ast.AddChild(ast_class);

    PopScope();
  }

  void CheckInterfaces(bhlParser.ClassDeclContext ctx, ClassSymbolScript class_symb)
  {
  }

  void ValidateInterfaceImplementation(bhlParser.ClassDeclContext ctx, InterfaceSymbol iface, ClassSymbolScript class_symb)
  {
    for(int i=0;i<iface.members.Count;++i)
    {
      var m = (FuncSymbol)iface.members[i];
      var func_symb = class_symb.Resolve(m.name) as FuncSymbol;
      if(func_symb == null || !Types.Is(func_symb.signature, m.signature))
        FireError(ctx, "class '" + class_symb.name + "' doesn't implement interface '" + iface.name + "' method '" + m + "'");
    }
  }

  public override object VisitEnumDecl(bhlParser.EnumDeclContext ctx)
  {
    var enum_name = ctx.NAME().GetText();

    //NOTE: currently all enum values are replaced with literals,
    //      so that it doesn't really make sense to create AST for them.
    //      But we do it just for consistency. Later once we have runtime 
    //      type info this will be justified.
    var symb = new EnumSymbolScript(enum_name);
    ns.Define(symb);

    for(int i=0;i<ctx.enumBlock().enumMember().Length;++i)
    {
      var em = ctx.enumBlock().enumMember()[i];
      var em_name = em.NAME().GetText();
      int em_val = int.Parse(em.INT().GetText(), System.Globalization.CultureInfo.InvariantCulture);

      int res = symb.TryAddItem(em_name, em_val);
      if(res == 1)
        FireError(em.NAME(), "duplicate key '" + em_name + "'");
      else if(res == 2)
        FireError(em.INT(), "duplicate value '" + em_val + "'");
    }

    return null;
  }

  void Pass_VisitGlobalVar(ParserPass pass)
  {
    PushAST((AST_Tree)pass.ast);
    PushScope(pass.scope);

    var vd = pass.gvar_ctx.varDeclare(); 

    if(being_imported)
    {
      var symb = new VariableSymbol(Wrap(vd.NAME()), vd.NAME().GetText(), ns.T(vd.type().GetText()));
      curr_scope.Define(symb);
    }
    else
    {
      var assign_exp = pass.gvar_ctx.assignExp();

      AST_Interim exp_ast = null;
      if(assign_exp != null)
      {
        var tp = ParseType(vd.type());

        exp_ast = new AST_Interim();
        PushAST(exp_ast);
        PushJsonType(tp.Get());
        Visit(assign_exp);
        PopJsonType();
        PopAST();
      }

      var ast = CommonDeclVar(curr_scope, vd.NAME(), vd.type(), is_ref: false, func_arg: true, write: assign_exp != null);

      if(exp_ast != null)
        PeekAST().AddChild(exp_ast);
      PeekAST().AddChild(ast);

      if(assign_exp != null)
        types.CheckAssign(Wrap(vd.NAME()), Wrap(assign_exp));
    }

    PopScope();
    PopAST();
  }

  public override object VisitFuncParams(bhlParser.FuncParamsContext ctx)
  {
    var fparams = ctx.funcParamDeclare();
    bool found_default_arg = false;

    for(int i=0;i<fparams.Length;++i)
    {
      var fp = fparams[i]; 
      if(fp.assignExp() != null)
      {
        if(curr_scope is LambdaSymbol)
          FireError(fp.NAME(), "default argument values not allowed for lambdas");

        found_default_arg = true;
      }
      else if(found_default_arg)
        FireError(fp.NAME(), "missing default argument expression");

      bool pop_json_type = false;
      if(found_default_arg)
      {
        var tp = ParseType(fp.type());
        PushJsonType(tp.Get());
        pop_json_type = true;
      }

      Visit(fp);

      if(pop_json_type)
        PopJsonType();
    }

    return null;
  }

  public override object VisitVarDecl(bhlParser.VarDeclContext ctx)
  {
    var vd = ctx.varDeclare(); 
    PeekAST().AddChild(CommonDeclVar(curr_scope, vd.NAME(), vd.type(), is_ref: false, func_arg: false, write: false));
    return null;
  }

  void CommonDeclOrAssign(bhlParser.VarDeclareOrCallExpContext[] vdecls, bhlParser.AssignExpContext assign_exp, int start_line)
  {
    var root = PeekAST();
    int root_first_idx = root.children.Count;

    IType assign_type = null;
    for(int i=0;i<vdecls.Length;++i)
    {
      var tmp = vdecls[i];
      var cexp = tmp.callExp();
      var vd = tmp.varDeclare();

      WrappedParseTree ptree = null;
      IType curr_type = null;
      bool is_decl = false;

      if(cexp != null)
      {
        if(assign_exp == null)
          FireError(cexp, "assign expression expected");
        else if(cexp.chainExp()?.Length > 0 && cexp.chainExp()[cexp.chainExp().Length-1].callArgs() != null)
          FireError(assign_exp, "invalid assignment");

        ProcChainedCall(cexp.DOT() != null ? ns : curr_scope, cexp.NAME(), cexp.chainExp(), ref curr_type, cexp.Start.Line, write: true);

        ptree = Wrap(cexp.NAME());
        ptree.eval_type = curr_type;
      }
      else 
      {
        var vd_type = vd.type();

        //check if we declare a var or use an existing one
        if(vd_type == null)
        {
          string vd_name = vd.NAME().GetText(); 
          var vd_symb = curr_scope.ResolveWithFallback(vd_name) as VariableSymbol;
          if(vd_symb == null)
            FireError(vd, "symbol not resolved");
          curr_type = vd_symb.type.Get();

          ptree = Wrap(vd.NAME());
          ptree.eval_type = curr_type;

          var ast = new AST_Call(EnumCall.VARW, start_line, vd_symb);
          root.AddChild(ast);
        }
        else
        {
          var ast = CommonDeclVar(curr_scope, vd.NAME(), vd_type, is_ref: false, func_arg: false, write: assign_exp != null);
          root.AddChild(ast);

          is_decl = true;

          ptree = Wrap(vd.NAME()); 
          curr_type = ptree.eval_type;
        }
      }

      //NOTE: if there is an assignment we have to visit it and push current
      //      json type if required
      if(assign_exp != null && assign_type == null)
      {
        //NOTE: look forward at expression and push json type 
        //      if it's a json-init-expression
        bool pop_json_type = false;
        if((assign_exp.exp() is bhlParser.ExpJsonObjContext || 
          assign_exp.exp() is bhlParser.ExpJsonArrContext))
        {
          if(vdecls.Length != 1)
            FireError(assign_exp, "multi assign not supported for JSON expression");

          pop_json_type = true;

          PushJsonType(curr_type);
        }

        //TODO: below is quite an ugly hack, fix it traversing the expression first
        //NOTE: temporarily replacing just declared variable with the dummy one when visiting 
        //      assignment expression in order to avoid error like: float k = k
        Symbol disabled_symbol = null;
        Symbol subst_symbol = null;
        if(is_decl)
        {
          var symbols = curr_scope.GetMembers();
          disabled_symbol = (Symbol)symbols[symbols.Count - 1];
          subst_symbol = new VariableSymbol(disabled_symbol.parsed, "#$"+disabled_symbol.name, disabled_symbol.type);
          symbols.Replace(disabled_symbol, subst_symbol);
        }

        //NOTE: need to put expression nodes first
        var stash = new AST_Interim();
        PushAST(stash);
        Visit(assign_exp);
        PopAST();
        for(int s=stash.children.Count;s-- > 0;)
          root.children.Insert(root_first_idx, stash.children[s]);

        assign_type = Wrap(assign_exp).eval_type;

        //NOTE: declaring disabled symbol again
        if(disabled_symbol != null)
        {
          var symbols = curr_scope.GetMembers();
          symbols.Replace(subst_symbol, disabled_symbol);
        }

        if(pop_json_type)
          PopJsonType();

        var tuple = assign_type as TupleType; 
        if(vdecls.Length > 1)
        {
          if(tuple == null)
            FireError(assign_exp, "multi return expected");

          if(tuple.Count != vdecls.Length)
            FireError(assign_exp, "multi return size doesn't match destination");
        }
        else if(tuple != null)
          FireError(assign_exp, "multi return size doesn't match destination");
      }

      if(assign_type != null)
      {
        var tuple = assign_type as TupleType;
        if(tuple != null)
          types.CheckAssign(ptree, tuple[i].Get());
        else
          types.CheckAssign(ptree, Wrap(assign_exp));
      }
    }
  }

  public override object VisitDeclAssign(bhlParser.DeclAssignContext ctx)
  {
    var vdecls = ctx.varsDeclareOrCallExps().varDeclareOrCallExp();
    var assign_exp = ctx.assignExp();

    CommonDeclOrAssign(vdecls, assign_exp, ctx.Start.Line);

    return null;
  }

  public override object VisitFuncParamDeclare(bhlParser.FuncParamDeclareContext ctx)
  {
    var name = ctx.NAME();
    var assign_exp = ctx.assignExp();
    bool is_ref = ctx.isRef() != null;
    bool is_null_ref = false;

    if(is_ref && assign_exp != null)
    {
      //NOTE: super special case for 'null refs'
      if(assign_exp.exp().GetText() == "null")
        is_null_ref = true;
      else
        FireError(name, "'ref' is not allowed to have a default value");
    }

    AST_Interim exp_ast = null;
    if(assign_exp != null)
    {
      exp_ast = new AST_Interim();
      PushAST(exp_ast);
      Visit(assign_exp);
      PopAST();
    }

    var ast = CommonDeclVar(curr_scope, name, ctx.type(), is_ref, func_arg: true, write: false);
    if(exp_ast != null)
      ast.AddChild(exp_ast);
    PeekAST().AddChild(ast);

    if(assign_exp != null && !is_null_ref)
      types.CheckAssign(Wrap(name), Wrap(assign_exp));
    return null;
  }

  AST_Tree CommonDeclVar(IScope curr_scope, ITerminalNode name, bhlParser.TypeContext type_ctx, bool is_ref, bool func_arg, bool write)
  {
    var tp = ParseType(type_ctx);

    var var_tree = Wrap(name); 
    var_tree.eval_type = tp.Get();

    if(is_ref && !func_arg)
      FireError(name, "'ref' is only allowed in function declaration");

    VariableSymbol symb = func_arg ? 
      (VariableSymbol) new FuncArgSymbol(var_tree, name.GetText(), tp, is_ref) :
      new VariableSymbol(var_tree, name.GetText(), tp);

    curr_scope.Define(symb);

    if(write)
      return new AST_Call(EnumCall.VARW, name.Symbol.Line, symb);
    else
      return new AST_VarDecl(symb, is_ref);
  }

  public override object VisitBlock(bhlParser.BlockContext ctx)
  {
    CommonVisitBlock(BlockType.SEQ, ctx.statement());
    return null;
  }

  public override object VisitFuncBlock(bhlParser.FuncBlockContext ctx)
  {
    CommonVisitBlock(BlockType.FUNC, ctx.block().statement());
    return null;
  }

  public override object VisitParal(bhlParser.ParalContext ctx)
  {
    CommonVisitBlock(BlockType.PARAL, ctx.block().statement());
    return null;
  }

  public override object VisitParalAll(bhlParser.ParalAllContext ctx)
  {
    CommonVisitBlock(BlockType.PARAL_ALL, ctx.block().statement());
    return null;
  }

  public override object VisitDefer(bhlParser.DeferContext ctx)
  {
    ++defer_stack;
    if(defer_stack > 1)
      FireError(ctx, "nested defers are not allowed");
    CommonVisitBlock(BlockType.DEFER, ctx.block().statement());
    --defer_stack;
    return null;
  }

  public override object VisitIf(bhlParser.IfContext ctx)
  {
    var ast = new AST_Block(BlockType.IF);

    var main = ctx.mainIf();

    var main_cond = new AST_Block(BlockType.SEQ);
    PushAST(main_cond);
    Visit(main.exp());
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(main.exp()));

    var func_symb = PeekFuncDecl();
    bool seen_return = return_found.Contains(func_symb);
    return_found.Remove(func_symb);

    ast.AddChild(main_cond);
    PushAST(ast);
    CommonVisitBlock(BlockType.SEQ, main.block().statement());
    PopAST();

    //NOTE: if in the block before there were no 'return' statements and in the current block
    //      *there's one* we need to reset the 'return found' flag since otherewise
    //      there's a code path without 'return', e.g:
    //
    //      func int foo() {
    //        if(..) {
    //          return 1
    //        } else {
    //          ...
    //        }
    //        return 3
    //      }
    //
    //      func int foo() {
    //        if(..) {
    //          ...
    //        } else {
    //          return 2
    //        }
    //        return 3
    //      }
    //
    //      func int foo() {
    //        if(..) {
    //          return 1 
    //        } else {
    //          return 2
    //        }
    //      }
    //
    if(!seen_return && return_found.Contains(func_symb) && (ctx.elseIf() == null || ctx.@else() == null))
      return_found.Remove(func_symb);

    var else_if = ctx.elseIf();
    for(int i=0;i<else_if.Length;++i)
    {
      var item = else_if[i];
      var item_cond = new AST_Block(BlockType.SEQ);
      PushAST(item_cond);
      Visit(item.exp());
      PopAST();

      types.CheckAssign(Types.Bool, Wrap(item.exp()));

      seen_return = return_found.Contains(func_symb);
      return_found.Remove(func_symb);

      ast.AddChild(item_cond);
      PushAST(ast);
      CommonVisitBlock(BlockType.SEQ, item.block().statement());
      PopAST();

      if(!seen_return && return_found.Contains(func_symb))
        return_found.Remove(func_symb);
    }

    var @else = ctx.@else();
    if(@else != null)
    {
      seen_return = return_found.Contains(func_symb);
      return_found.Remove(func_symb);

      PushAST(ast);
      CommonVisitBlock(BlockType.SEQ, @else.block().statement());
      PopAST();

      if(!seen_return && return_found.Contains(func_symb))
        return_found.Remove(func_symb);
    }

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitExpTernaryIf(bhlParser.ExpTernaryIfContext ctx)
  {
    var ast = new AST_Block(BlockType.IF); //short-circuit evaluation

    var exp_0 = ctx.exp();
    var exp_1 = ctx.ternaryIfExp().exp(0);
    var exp_2 = ctx.ternaryIfExp().exp(1);

    var condition = new AST_Interim();
    PushAST(condition);
    Visit(exp_0);
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(exp_0));

    ast.AddChild(condition);

    var consequent = new AST_Interim();
    PushAST(consequent);
    Visit(exp_1);
    PopAST();

    ast.AddChild(consequent);

    var alternative = new AST_Interim();
    PushAST(alternative);
    Visit(exp_2);
    PopAST();

    ast.AddChild(alternative);

    var wrap_exp_1 = Wrap(exp_1);
    Wrap(ctx).eval_type = wrap_exp_1.eval_type;

    types.CheckAssign(wrap_exp_1, Wrap(exp_2));
    PeekAST().AddChild(ast);
    return null;
  }

  public override object VisitWhile(bhlParser.WhileContext ctx)
  {
    var ast = new AST_Block(BlockType.WHILE);

    ++loops_stack;

    var cond = new AST_Block(BlockType.SEQ);
    PushAST(cond);
    Visit(ctx.exp());
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(ctx.exp()));

    ast.AddChild(cond);

    PushAST(ast);
    CommonVisitBlock(BlockType.SEQ, ctx.block().statement());
    PopAST();
    ast.children[ast.children.Count-1].AddChild(new AST_Continue(jump_marker: true));

    --loops_stack;

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitDoWhile(bhlParser.DoWhileContext ctx)
  {
    var ast = new AST_Block(BlockType.DOWHILE);

    ++loops_stack;

    PushAST(ast);
    CommonVisitBlock(BlockType.SEQ, ctx.block().statement());
    PopAST();
    ast.children[ast.children.Count-1].AddChild(new AST_Continue(jump_marker: true));

    var cond = new AST_Block(BlockType.SEQ);
    PushAST(cond);
    Visit(ctx.exp());
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(ctx.exp()));

    ast.AddChild(cond);

    --loops_stack;

    PeekAST().AddChild(ast);

    return null;
  }

  public override object VisitFor(bhlParser.ForContext ctx)
  {
    //NOTE: we're going to generate the following code
    //
    //<pre code>
    //while(<condition>)
    //{
    // ...
    // <post iter code>
    //}

    var local_scope = new LocalScope(false, curr_scope);
    PushScope(local_scope);
    local_scope.Enter();
    
    var for_pre = ctx.forExp().forPre();
    if(for_pre != null)
    {
      var for_pre_stmts = for_pre.forStmts();
      for(int i=0;i<for_pre_stmts.forStmt().Length;++i)
      {
        var stmt = for_pre_stmts.forStmt()[i];
        var vdoce = stmt.varsDeclareOrCallExps();
        
        if(vdoce != null)
        {
          var pre_vdecls = vdoce.varDeclareOrCallExp();
          var pre_assign_exp = stmt.assignExp();
          CommonDeclOrAssign(pre_vdecls, pre_assign_exp, ctx.Start.Line);
        }
        else
        {
          var cpo = stmt.callPostOperators();
          if(cpo != null)
            CommonVisitCallPostOperators(cpo);
        }
      }
    }

    var for_cond = ctx.forExp().forCond();
    var for_post_iter = ctx.forExp().forPostIter();

    var ast = new AST_Block(BlockType.WHILE);

    ++loops_stack;

    var cond = new AST_Block(BlockType.SEQ);
    PushAST(cond);
    Visit(for_cond);
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(for_cond.exp()));

    ast.AddChild(cond);

    PushAST(ast);
    var block = CommonVisitBlock(BlockType.SEQ, ctx.block().statement());
    //appending post iteration code
    if(for_post_iter != null)
    {
      PushAST(block);
      
      PeekAST().AddChild(new AST_Continue(jump_marker: true));
      var for_post_iter_stmts = for_post_iter.forStmts();
      for(int i=0;i<for_post_iter_stmts.forStmt().Length;++i)
      {
        var stmt = for_post_iter_stmts.forStmt()[i];
        var vdoce = stmt.varsDeclareOrCallExps();
        
        if(vdoce != null)
        {
          var post_vdecls = vdoce.varDeclareOrCallExp();
          var post_assign_exp = stmt.assignExp();
          CommonDeclOrAssign(post_vdecls, post_assign_exp, ctx.Start.Line);
        }
        else
        {
          var cpo = stmt.callPostOperators();
          if(cpo != null)
            CommonVisitCallPostOperators(cpo);
        }
      }
      PopAST();
    }
    PopAST();

    --loops_stack;

    PeekAST().AddChild(ast);

    local_scope.Exit();
    PopScope();

    return null;
  }

  public override object VisitYield(bhlParser.YieldContext ctx)
  {
     int line = ctx.Start.Line;
     var ast = new AST_Call(EnumCall.FUNC, line, ns.Resolve("yield"));
     PeekAST().AddChild(ast);
     return null;
  }

  public override object VisitYieldWhile(bhlParser.YieldWhileContext ctx)
  {
    //NOTE: we're going to generate the following code
    //while(cond) { yield() }

    var ast = new AST_Block(BlockType.WHILE);

    ++loops_stack;

    var cond = new AST_Block(BlockType.SEQ);
    PushAST(cond);
    Visit(ctx.exp());
    PopAST();

    types.CheckAssign(Types.Bool, Wrap(ctx.exp()));

    ast.AddChild(cond);

    var body = new AST_Block(BlockType.SEQ);
    int line = ctx.Start.Line;
    body.AddChild(new AST_Call(EnumCall.FUNC, line, ns.Resolve("yield")));
    ast.AddChild(body);

    --loops_stack;

    PeekAST().AddChild(ast);
    return null;
  }

  public override object VisitForeach(bhlParser.ForeachContext ctx)
  {
    //NOTE: we're going to generate the following code
    //
    //$foreach_tmp = arr
    //$foreach_cnt = 0
    //while($foreach_cnt < $foreach_tmp.Count)
    //{
    // arr_it = $foreach_tmp[$foreach_cnt]
    // ...
    // $foreach_cnt++
    //}
    
    var local_scope = new LocalScope(false, curr_scope);
    PushScope(local_scope);
    local_scope.Enter();

    var vod = ctx.foreachExp().varOrDeclare();
    var vd = vod.varDeclare();
    TypeProxy iter_type;
    string iter_str_name = "";
    AST_Tree iter_ast_decl = null;
    VariableSymbol iter_symb = null;
    if(vod.NAME() != null)
    {
      iter_str_name = vod.NAME().GetText();
      iter_symb = curr_scope.ResolveWithFallback(iter_str_name) as VariableSymbol;
      if(iter_symb == null)
        FireError(vod.NAME(), "symbol is not a valid variable");
      iter_type = iter_symb.type;
    }
    else
    {
      iter_str_name = vd.NAME().GetText();
      iter_ast_decl = CommonDeclVar(curr_scope, vd.NAME(), vd.type(), is_ref: false, func_arg: false, write: false);
      iter_symb = curr_scope.ResolveWithFallback(iter_str_name) as VariableSymbol;
      iter_type = iter_symb.type;
    }
    var arr_type = (ArrayTypeSymbol)ns.TArr(iter_type).Get();

    PushJsonType(arr_type);
    var exp = ctx.foreachExp().exp();
    //evaluating array expression
    Visit(exp);
    PopJsonType();
    types.CheckAssign(Wrap(exp), arr_type);

    var arr_tmp_name = "$foreach_tmp" + exp.Start.Line + "_" + exp.Start.Column;
    var arr_tmp_symb = curr_scope.ResolveWithFallback(arr_tmp_name) as VariableSymbol;
    if(arr_tmp_symb == null)
    {
      arr_tmp_symb = new VariableSymbol(Wrap(exp), arr_tmp_name, ns.T(iter_type));
      curr_scope.Define(arr_tmp_symb);
    }

    var arr_cnt_name = "$foreach_cnt" + exp.Start.Line + "_" + exp.Start.Column;
    var arr_cnt_symb = curr_scope.ResolveWithFallback(arr_cnt_name) as VariableSymbol;
    if(arr_cnt_symb == null)
    {
      arr_cnt_symb = new VariableSymbol(Wrap(exp), arr_cnt_name, ns.T("int"));
      curr_scope.Define(arr_cnt_symb);
    }

    PeekAST().AddChild(new AST_Call(EnumCall.VARW, ctx.Start.Line, arr_tmp_symb));
    //declaring counter var
    PeekAST().AddChild(new AST_VarDecl(arr_cnt_symb, is_ref: false));

    //declaring iterating var
    if(iter_ast_decl != null)
      PeekAST().AddChild(iter_ast_decl);

    var ast = new AST_Block(BlockType.WHILE);

    ++loops_stack;

    //adding while condition
    var cond = new AST_Block(BlockType.SEQ);
    var bin_op = new AST_BinaryOpExp(EnumBinaryOp.LT, ctx.Start.Line);
    bin_op.AddChild(new AST_Call(EnumCall.VAR, ctx.Start.Line, arr_cnt_symb));
    bin_op.AddChild(new AST_Call(EnumCall.VAR, ctx.Start.Line, arr_tmp_symb));
    bin_op.AddChild(new AST_Call(EnumCall.MVAR, ctx.Start.Line, arr_type.Resolve("Count")));
    cond.AddChild(bin_op);
    ast.AddChild(cond);

    PushAST(ast);
    var block = CommonVisitBlock(BlockType.SEQ, ctx.block().statement());
    //prepending filling of the iterator var
    block.children.Insert(0, new AST_Call(EnumCall.VARW, ctx.Start.Line, iter_symb));
    var arr_at = new AST_Call(EnumCall.ARR_IDX, ctx.Start.Line, null);
    block.children.Insert(0, arr_at);
    block.children.Insert(0, new AST_Call(EnumCall.VAR, ctx.Start.Line, arr_cnt_symb));
    block.children.Insert(0, new AST_Call(EnumCall.VAR, ctx.Start.Line, arr_tmp_symb));

    block.AddChild(new AST_Continue(jump_marker: true));
    //appending counter increment
    block.AddChild(new AST_Inc(arr_cnt_symb));
    PopAST();

    --loops_stack;

    PeekAST().AddChild(ast);

    local_scope.Exit();
    PopScope();

    return null;
  }

  AST_Tree CommonVisitBlock(BlockType type, IParseTree[] sts)
  {
    bool is_paral = 
      type == BlockType.PARAL || 
      type == BlockType.PARAL_ALL;

    var local_scope = new LocalScope(is_paral, curr_scope);
    PushScope(local_scope);
    local_scope.Enter();

    var ast = new AST_Block(type);
    var tmp = new AST_Interim();
    PushAST(ast);
    for(int i=0;i<sts.Length;++i)
    {
      //NOTE: we need to understand if we need to wrap statements
      //      with a group 
      if(is_paral)
      {
        PushAST(tmp);

        Visit(sts[i]);

        PopAST();
        //NOTE: wrapping in group only in case there are more than one child
        if(tmp.children.Count > 1)
        {
          var seq = new AST_Block(BlockType.SEQ);
          for(int c=0;c<tmp.children.Count;++c)
            seq.AddChild(tmp.children[c]);
          ast.AddChild(seq);
        }
        else
          ast.AddChild(tmp.children[0]);
        tmp.children.Clear();
      }
      else
        Visit(sts[i]);
    }
    PopAST();

    local_scope.Exit();
    PopScope();

    if(is_paral)
      return_found.Remove(PeekFuncDecl());

    PeekAST().AddChild(ast);
    return ast;
  }
}

} //namespace bhl
