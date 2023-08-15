using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace bhl.lsp {

public interface IService 
{
  //TODO:
  //public void Tick();
}

public interface IPublisher
{
  void Publish(Notification notification);
}

public interface IRpcHandler
{
  Response HandleRequest(Request request);
}

public abstract class MessageBase
{
  public string jsonrpc { get; set; } = "2.0";

  public bool IsValid()
  {
    return jsonrpc == "2.0";
  }
}

public class Request : MessageBase
{
  public proto.EitherType<Int32, Int64, string> id { get; set; }
  public string method { get; set; }
  public JToken @params { get; set; }

  //for tests convenience
  public Request(int id, string method, object @params = null)
  {
    this.id = id;
    this.method = method;
    if(@params != null)
      this.@params = JToken.FromObject(@params); 
  }
}

public class ResponseError
{
  public int code { get; set; }
  public string message { get; set; }
  public object data { get; set; }
}

public class Response : MessageBase
{
  public proto.EitherType<Int32, Int64, string> id { get; set; }
  public object result { get; set; }
  public ResponseError error { get; set; }

  public Response()
  {}

  //for tests convenience
  public Response(int id, object result, ResponseError error = null)
  {
    this.id = id;
    this.result = result;
    this.error = error;
  }
}

public class Notification 
{
  public string method { get; set; }
  public object @params { get; set; }
}

public class RpcServer : IRpcHandler, IPublisher
{
  IConnection connection;
  Logger logger;

  public struct ServiceMethod
  {
    public IService service;
    public MethodInfo method;
    public System.Type arg_type;
  }

  List<IService> services = new List<IService>();
  Dictionary<string, ServiceMethod> name2method = new Dictionary<string, ServiceMethod>();

  public bool need_to_exit { get; private set; } = false;

  public RpcServer(Logger logger, IConnection connection)
  {
    this.logger = logger;
    this.connection = connection;
  }

  public void AttachService(IService service)
  {
    foreach(var method in service.GetType().GetMethods())
    {
      string name = FindRpcMethodName(method);
      if(name != null)
      {
        var sm = new ServiceMethod();
        sm.service = service;
        sm.method = method;

        var pms = method.GetParameters();
        if(pms.Length == 1)
          sm.arg_type = pms[0].ParameterType; 
        else if(pms.Length == 0)
          sm.arg_type = null;
        else
          throw new Exception("Too many method arguments count");

        name2method.Add(name, sm);
      }
    }
    services.Add(service);
  }

  public void Start()
  {
    logger.Log(1, "Starting BHL LSP server...");

    while(true)
    {
      try
      {
        bool success = ReadAndHandle();
        if(!success)
          break;
      }
      catch(Exception e)
      {
        logger.Log(0, e.ToString());
        break;
      }
    }

    logger.Log(1, "Stopping BHL LSP server...");
  }

  bool ReadAndHandle()
  {
    try
    {
      string json = connection.Read();
      if(string.IsNullOrEmpty(json))
        return false;
    
      string response = Handle(json);

      if(!string.IsNullOrEmpty(response))
        connection.Write(response);
      else if(need_to_exit)
        return false;
    }
    catch(Exception e)
    {
      logger.Log(0, e.ToString());
      return false;
    }
    
    return true;
  }

  public string Handle(string req_json)
  {
    var sw = Stopwatch.StartNew();
    
    Request req = null;
    Response rsp = null;
    
    try
    {
      req = req_json.FromJson<Request>();
    }
    catch(Exception e)
    {
      logger.Log(0, e.ToString());
      logger.Log(0, req_json);

      rsp = new Response
      {
        error = new ResponseError
        {
          code = (int)ErrorCodes.ParseError,
          message = "Parse error"
        }
      };
    }

    string resp_json = string.Empty;

    logger.Log(1, $"> ({req?.method}, id: {req?.id.Value}) {(req_json.Length > 500 ? req_json.Substring(0,500) + ".." : req_json)}");
    
    //NOTE: if there's no response error by this time 
    //      let's handle the request
    if(req != null && rsp == null)
    {
      if(req.IsValid())
        rsp = HandleRequest(req);
      else
        rsp = new Response
        {
          error = new ResponseError
          {
            code = (int)ErrorCodes.InvalidRequest,
            message = ""
          }
        };
    }

    if(rsp != null)
    {
      //NOTE: handling special type of response: server exit 
      if((rsp.error?.code??0) == (int)ErrorCodes.Exit)
        need_to_exit = true;
      else if(rsp.error != null || rsp.result != null)
        resp_json = rsp.ToJson();
      //NOTE: special 'null-result' case: we need the null result to be sent to the client,
      //      however null values are omitted when serialized to JSON, hence the hack
      else if(rsp.result == null)
        resp_json = rsp.ToJson().TrimEnd('}') + ",\"result\":null}";
    }

    sw.Stop();
    logger.Log(1, $"< ({req?.method}, id: {req?.id.Value}) done({Math.Round(sw.ElapsedMilliseconds/1000.0f,2)} sec) {(resp_json?.Length > 500 ? resp_json?.Substring(0,500) + ".." : resp_json)}");
    
    return resp_json;
  }
  
  public Response HandleRequest(Request request)
  {
    Response response;
    
    try
    {
      if(!string.IsNullOrEmpty(request.method))
      {
        RpcResult result = CallRpcMethod(request.method, request.@params);
        if(result != null)
        {
          response = new Response
          {
            id = request.id,
            result = result.result,
            error = result.error
          };
        }
        else
          response = null;
      }
      else
      {
        response = new Response
        {
          id = request.id, 
          error = new ResponseError
          {
            code = (int)ErrorCodes.InvalidRequest,
            message = ""
          }
        };
      }
    }
    catch(Exception e)
    {
      logger.Log(0, e.ToString());

      response = new Response
      {
        id = request.id, 
        error = new ResponseError
        {
          code = (int)ErrorCodes.InternalError,
          message = e.ToString()
        }
      };
    }
    
    return response;
  }

  RpcResult CallRpcMethod(string name, JToken @params)
  {
    if(double.TryParse(name, out _))
    {
      return RpcResult.Error(new ResponseError
      {
        code = (int)ErrorCodes.InvalidRequest,
        message = ""
      });
    }

    if(!name2method.TryGetValue(name, out var sm)) 
    {
      return RpcResult.Error(new ResponseError
      {
        code = (int)ErrorCodes.MethodNotFound,
        message = "Method not found"
      });
    }
    
    object[] args = null;
    if(sm.arg_type != null)
    {
      try
      {
        args = new[] { @params.ToObject(sm.arg_type) };
      }
      catch(Exception e)
      {
        logger.Log(0, e.ToString());

        return RpcResult.Error(new ResponseError
        {
          code = (int)ErrorCodes.InvalidParams,
          message = e.Message
        });
      }
    }
    
    return (RpcResult)sm.method.Invoke(sm.service, args);
  }

  public void Publish(Notification notification)
  {
    connection.Write(notification.ToJson());
  }
  
  static string FindRpcMethodName(MethodInfo m)
  {
    foreach(var attribute in m.GetCustomAttributes(true))
    {
      if(attribute is RpcMethod rm)
        return rm.Method;
    }

    return null;
  }
}

public class RpcResult
{
  public object result;
  public ResponseError error;
  
  public static RpcResult Error(ResponseError error)
  {
    return new RpcResult(null, error);
  }

  public static RpcResult Error(ErrorCodes code, string msg = "")
  {
    return new RpcResult(null, new ResponseError() { code = (int)code, message = msg });
  }

  public RpcResult(object result, ResponseError error = null)
  {
    this.result = result;
    this.error = error;
  }
}

public enum ErrorCodes
{
  Exit = -1, //special error code which makes the server to exit

  ParseError = -32700,
  InvalidRequest = -32600,
  MethodNotFound = -32601,
  InvalidParams = -32602,
  InternalError = -32603,
  ServerErrorStart = -32099,
  ServerErrorEnd = -32000,
  ServerNotInitialized = -32002,
  UnknownErrorCode = -32001,
  RequestCancelled = -32800,
  RequestFailed = -32803 // @since 3.17.0
}

[AttributeUsage(AttributeTargets.Method)]
public class RpcMethod : Attribute
{
  private string method;

  public RpcMethod(string method)
  {
    this.method = method;
  }

  public string Method => method;
}

}
