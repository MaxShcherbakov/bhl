using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using bhlsp;

public class TestLSP : BHL_TestBase
{
  [IsTested()]
  public void TestRpcResponseErrors()
  {
    string json = "";
    var rpc = new BHLSPJsonRpc();

    //ParseError
    json = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"},\"jsonrpc\":\"2.0\"}"
    );
    
    //InvalidRequest
    json = "";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":null,\"error\":{\"code\":-32600,\"message\":\"\"},\"jsonrpc\":\"2.0\"}"
    );
    
    json = "{\"jsonrpc\": \"2.0\", \"id\": 1}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":1,\"error\":{\"code\":-32600,\"message\":\"\"},\"jsonrpc\":\"2.0\"}"
    );
    
    json = "{\"jsonrpc\": \"2.0\", \"method\": 1, \"params\": \"bar\"}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":null,\"error\":{\"code\":-32600,\"message\":\"\"},\"jsonrpc\":\"2.0\"}"
    );
    
    //MethodNotFound
    json = "{\"jsonrpc\": \"2.0\", \"method\": \"foo\", \"id\": 1}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":1,\"error\":{\"code\":-32601,\"message\":\"Method not found\"},\"jsonrpc\":\"2.0\"}"
    );
    
    //InvalidParams
    rpc.AttachRpcService(new BHLSPGeneralJsonRpcService());
    json = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"params\": \"bar\"}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":null,\"error\":{\"code\":-32602,\"message\":\"\"},\"jsonrpc\":\"2.0\"}"
    );
    
    json = "{\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"params\": \"bar\", \"id\": 1}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":1,\"error\":{\"code\":-32602,\"message\":\"\"},\"jsonrpc\":\"2.0\"}"
    );
  }

  [IsTested()]
  public void TestGeneralRpc()
  {
    string json = "";
    string rsp = "";
    
    var rpc = new BHLSPJsonRpc();
    rpc.AttachRpcService(new BHLSPGeneralJsonRpcService());
    
    json = "{\"id\": 1,\"jsonrpc\": \"2.0\", \"method\": \"initialize\", \"params\": {\"capabilities\":{}}}";
    
    rsp = "{\"id\":1,\"result\":{" +
            "\"capabilities\":{" +
              "\"textDocumentSync\":null," +
              "\"hoverProvider\":null," +
              "\"declarationProvider\":null," +
              "\"definitionProvider\":null," +
              "\"typeDefinitionProvider\":null," +
              "\"implementationProvider\":null," +
              "\"referencesProvider\":null," +
              "\"documentHighlightProvider\":null," +
              "\"documentSymbolProvider\":null," +
              "\"codeActionProvider\":null," +
              "\"colorProvider\":null," +
              "\"documentFormattingProvider\":null," +
              "\"documentRangeFormattingProvider\":null," +
              "\"renameProvider\":null," +
              "\"foldingRangeProvider\":null," +
              "\"selectionRangeProvider\":null," +
              "\"linkedEditingRangeProvider\":null," +
              "\"callHierarchyProvider\":null," +
              "\"semanticTokensProvider\":null," +
              "\"monikerProvider\":null," +
              "\"workspaceSymbolProvider\":null}," +
            "\"serverInfo\":{\"name\":\"bhlsp\",\"version\":\"0.1\"}}," +
            "\"jsonrpc\":\"2.0\"}";

    AssertEqual(rpc.HandleMessage(json), rsp);
    
    json = "{\"params\":{},\"method\":\"initialized\",\"jsonrpc\":\"2.0\"}";
    AssertEqual(
      rpc.HandleMessage(json),
      string.Empty
    );
    
    json = "{\"id\":1,\"params\":null,\"method\":\"shutdown\",\"jsonrpc\":\"2.0\"}";
    AssertEqual(
      rpc.HandleMessage(json),
      "{\"id\":1,\"result\":\"null\",\"jsonrpc\":\"2.0\"}"
    );
    
    json = "{\"params\":null,\"method\":\"exit\",\"jsonrpc\":\"2.0\"}";
    AssertEqual(
      rpc.HandleMessage(json),
      string.Empty
    );
  }
}