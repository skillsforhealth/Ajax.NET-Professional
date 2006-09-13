/*
 * MS	06-04-13	changed content type to "application/json; charset=utf-8"
 * MS	06-04-25	fixed content type, changed back to "text/plain; charset=utf-8"
 * MS	06-04-28	added http header value AjaxPro-Cache
 * MS	06-05-03	changed content type use
 *					added WebAjaxErrorEvent
 * MS	06-05-23	using cached GetCustomAttributes type array from IAjaxProcessor
 * MS	06-05-30	changed http header to X-AjaxPro
 * MS	06-06-02	fixed AjaxServerCache key
 * MS	06-06-06	using ContentType from IAjaxProcessor
 * MS	06-06-11	removed WebEvent because of SecurityPermissions not available in medium trust environments
 * 
 * 
 * 
 */
using System;
using System.Reflection;
using System.Web;
using System.Web.Caching;
using System.IO;
#if(NET20)
using System.Web.Management;
#endif

namespace AjaxPro
{
	internal class AjaxProcHelper
	{
		private IAjaxProcessor p;
		private IntPtr token = IntPtr.Zero;
		private System.Security.Principal.WindowsImpersonationContext winctx = null;

		internal AjaxProcHelper(IAjaxProcessor p)
		{
			this.p = p;
		}

		internal AjaxProcHelper(IAjaxProcessor p, IntPtr token) : this(p)
		{
			this.token = token;
		}

		internal void Run()
		{
			if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Begin ProcessRequest");


			try
			{
				// If we are using the async handler we have to set the ASPNET username
				// to have the same user rights for the created thread.

				if(token != IntPtr.Zero)
					winctx = System.Security.Principal.WindowsIdentity.Impersonate(token);


				// We will check the custom attributes and try to invoke the method.
		
				p.Context.Response.Expires = 0;
				p.Context.Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);

				// TODO: check why Opera is not working with application/json;
				// p.Context.Response.AddHeader("Content-Type", "application/json; charset=utf-8");

				p.Context.Response.ContentType = p.ContentType;
				p.Context.Response.ContentEncoding = System.Text.Encoding.UTF8;

				if(!p.IsValidAjaxToken(Utility.CurrentAjaxToken))
				{
					p.SerializeObject(new System.Security.SecurityException("The AjaxPro-Token is not valid."));

					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					return;
				}

				object[] po = null;
				object res = null;

				#region Retreive Parameters from the HTTP Request

				try
				{
					// The IAjaxProcessor will read the values either form the 
					// request URL or the request input stream.

					po = p.RetreiveParameters();
				}
				catch(Exception ex)
				{
					p.SerializeObject(ex);

					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					return;
				}

				#endregion 

				// Check if we have the same request already in our cache. The 
				// cacheKey will be the type and a hashcode from the parameter values.

				string cacheKey = p.Type.FullName + "|" + p.GetType().Name + "|" + p.AjaxMethod.Name + "|" + p.GetHashCode();
				if(p.Context.Cache[cacheKey] != null)
				{
					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Using cached result");

					p.Context.Response.AddHeader("X-" + Constant.AjaxID + "-Cache", "server");

					// Return the full output of the last cached call
					p.Context.Response.Write(p.Context.Cache[cacheKey]);

					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					return;
				}

				#region Reflection part of Ajax.NET

				try
				{
					if (p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Invoking " + p.Type.FullName + "." + p.AjaxMethod.Name);

					// If this is a static method we do not need to create an instance
					// of this class. Some classes do not have a default constructor.

					if (p.AjaxMethod.IsStatic)
					{
						try
						{
							res = p.Type.InvokeMember(
								p.AjaxMethod.Name,
								System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.IgnoreCase,
								null, null, po);
						}
						catch(Exception ex)
						{
							if(ex.InnerException != null)
								p.SerializeObject(ex.InnerException);
							else
								p.SerializeObject(ex);

							if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
							return;
						}
					}
					else
					{
						// Create an instance of the class using the default constructor that will
						// not need any argument. This can be a problem, but currently I have no
						// idea on how to specify arguments for the constructor.

						if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Reflection Start");

						try
						{
							object c = (object)Activator.CreateInstance(p.Type, new object[]{});

							// Because the page context properties (Request, Response, Cache...) are 
							// not set using Reflection we will set the context by using the IContextInitializer
							// interface.

							if(typeof(IContextInitializer).IsAssignableFrom(p.Type))
							{
								((IContextInitializer)c).InitializeContext(p.Context);
							}

							if(c != null)
							{
//								if(po == null)
//									po = new object[p.Method.GetParameters().Length];

								res = p.AjaxMethod.Invoke(c, po);
							}
						}
						catch(Exception ex)
						{
							string errorText = string.Format(Constant.AjaxID + " Error", p.Context.User.Identity.Name);

							if (ex.InnerException != null)
							{
#if(WEBEVENT)
								Management.WebAjaxErrorEvent ev = new Management.WebAjaxErrorEvent(errorText, p, po, WebEventCodes.WebExtendedBase + 100, ex.InnerException);
								ev.Raise();
#endif
								p.SerializeObject(ex.InnerException);
							}
							else
							{
#if(WEBEVENT)
								Management.WebAjaxErrorEvent ev = new Management.WebAjaxErrorEvent(errorText, p, po, WebEventCodes.WebExtendedBase + 101, ex);
								ev.Raise();
#endif
								p.SerializeObject(ex);
							}

							if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
							return;
						}

						if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Reflection End");

					}
				}
				catch(Exception ex)
				{
					if(ex.InnerException != null)
						p.SerializeObject(ex.InnerException);
					else
						p.SerializeObject(ex);

					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					return;
				}

				#endregion


				try
				{
					if(res != null && res.GetType() == typeof(System.Xml.XmlDocument))
					{
						// If the return value is XmlDocument we will return it direct
						// without any convertion. On the client-side function we can
						// use .responseXML or .xml.

						p.Context.Response.ContentType = "text/xml";
						((System.Xml.XmlDocument)res).Save(p.Context.Response.OutputStream);


						if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
						return;
					}


					string result = null;;

					System.Text.StringBuilder sb = new System.Text.StringBuilder();

					try
					{
						result = p.SerializeObject(res);
					}
					catch(Exception ex)
					{
						p.SerializeObject(ex);

						if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
						return;
					}

					if(p.ServerCacheAttributes.Length > 0)
					{
						if (p.ServerCacheAttributes[0].IsCacheEnabled)
						{
							p.Context.Cache.Add(cacheKey, result, null, DateTime.Now.Add(p.ServerCacheAttributes[0].CacheDuration), System.Web.Caching.Cache.NoSlidingExpiration, System.Web.Caching.CacheItemPriority.Normal, null);
							if (p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "Adding result to cache for " + p.ServerCacheAttributes[0].CacheDuration.TotalSeconds + " seconds (HashCode = " + p.GetHashCode().ToString() + ")");
						}
					}

					if(p.Context.Trace.IsEnabled)
					{
						p.Context.Trace.Write(Constant.AjaxID, "Result (maybe encrypted): " + result);
						p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					}
				}
				catch(Exception ex)
				{
					p.SerializeObject(ex);

					if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
					return;
				}
				
			}
			catch(Exception ex)
			{
				p.SerializeObject(ex);

				if(p.Context.Trace.IsEnabled) p.Context.Trace.Write(Constant.AjaxID, "End ProcessRequest");
				return;
			}
			finally
			{
				if(token != IntPtr.Zero)
					winctx.Undo();
			}
		}
	}
}