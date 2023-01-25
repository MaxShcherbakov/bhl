using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace bhl.lsp {

public class BHLDocument
{
  public Uri uri;
  
  public Code Code = new Code();

  Parser parser = new Parser();

  List<IParseTree> rules = new List<IParseTree>();
  
  public List<string> Imports => parser.imports;
  public Dictionary<string, bhlParser.ClassDeclContext> ClassDecls => parser.class_decls;
  public Dictionary<string, bhlParser.FuncDeclContext> FuncDecls => parser.func_decls;
  public List<uint> DataSemanticTokens => parser.encoded_semantic_tokens;
  
  public void Update(string text)
  {
    Code.Update(text);

    parser.Parse(this);

    foreach(var rule in Parser.IterateRules(ToParser().program()))
      rules.Add(rule);
  }

  public ParserRuleContext FindParserRule(int line, int character)
  {
    return FindParserRuleByIndex(Code.CalcByteIndex(line, character));
  }

  public ParserRuleContext FindParserRuleByIndex(int idx)
  {
    //TODO: use binary search?
    foreach(var node in rules)
    {
      if(node is ParserRuleContext ctx && ctx.Start.StartIndex <= idx && ctx.Stop.StopIndex >= idx)
        return ctx;
    }
    return null;
  }

  public bhlParser ToParser()
  {
    var ais = new AntlrInputStream(Code.Text.ToStream());
    var lex = new bhlLexer(ais);
    var tokens = new CommonTokenStream(lex);
    var parser = new bhlParser(tokens);
    
    lex.RemoveErrorListeners();
    parser.RemoveErrorListeners();

    return parser;
  }
}

public static class BHLSemanticTokens
{
  public static string[] token_types = 
  {
    spec.SemanticTokenTypes.@class,
    spec.SemanticTokenTypes.function,
    spec.SemanticTokenTypes.variable,
    spec.SemanticTokenTypes.number,
    spec.SemanticTokenTypes.@string,
    spec.SemanticTokenTypes.type,
    spec.SemanticTokenTypes.keyword
  };
  
  public static string[] modifiers = 
  {
    spec.SemanticTokenModifiers.declaration,   // 1
    spec.SemanticTokenModifiers.definition,    // 2
    spec.SemanticTokenModifiers.@readonly,     // 4
    spec.SemanticTokenModifiers.@static,       // 8
    spec.SemanticTokenModifiers.deprecated,    // 16
    spec.SemanticTokenModifiers.@abstract,     // 32
    spec.SemanticTokenModifiers.async,         // 64
    spec.SemanticTokenModifiers.modification,  // 128
    spec.SemanticTokenModifiers.documentation, // 256
    spec.SemanticTokenModifiers.defaultLibrary // 512
  };
}

}
