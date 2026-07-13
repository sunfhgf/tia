using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TiaAutomation.Api
{
    /// <summary>
    /// 简易 path 路由：支持 "/api/projects/{id}" 这样的占位。
    /// </summary>
    public class Router
    {
        private readonly List<Route> _routes = new List<Route>();

        public Router Map(string method, string template, Func<RequestContext, Task> handler)
        {
            _routes.Add(new Route(method.ToUpperInvariant(), template, handler));
            return this;
        }

        public RouteMatch Match(string method, string path)
        {
            method = (method ?? "").ToUpperInvariant();
            foreach (var route in _routes)
            {
                if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
                var m = route.Regex.Match(path);
                if (!m.Success) continue;
                var pathParams = new Dictionary<string, string>();
                foreach (var name in route.ParamNames)
                {
                    pathParams[name] = m.Groups[name].Value;
                }
                return new RouteMatch { Handler = route.Handler, PathParams = pathParams };
            }
            return null;
        }

        private class Route
        {
            public string Method { get; }
            public string Template { get; }
            public Regex Regex { get; }
            public List<string> ParamNames { get; } = new List<string>();
            public Func<RequestContext, Task> Handler { get; }

            public Route(string method, string template, Func<RequestContext, Task> handler)
            {
                Method = method;
                Template = template;
                Handler = handler;

                // /api/projects/{id} -> ^/api/projects/(?<id>[^/]+)$
                var pattern = "^" + Regex.Replace(template, "{([^}]+)}", m =>
                {
                    var name = m.Groups[1].Value;
                    ParamNames.Add(name);
                    return "(?<" + name + ">[^/]+)";
                }) + "$";
                Regex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
        }
    }

    public class RouteMatch
    {
        public Func<RequestContext, Task> Handler { get; set; }
        public Dictionary<string, string> PathParams { get; set; }
    }
}
