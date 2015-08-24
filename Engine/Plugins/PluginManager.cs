﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using System.Threading;

namespace Engine.Plugins
{
  public abstract class PluginManager<TPlugin, TModel> : IDisposable
    where TPlugin : Plugin<TModel>
    where TModel : CrossDomainObject, new()
  {
    private const int ProcessTimeout = 10000; // 10 sec

    #region nested types

    protected class PluginContainer
    {
      public AppDomain Domain { get; private set; }
      public TPlugin Plugin { get; private set; }

      public PluginContainer(AppDomain domain, TPlugin plugin)
      {
        Domain = domain;
        Plugin = plugin;
      }
    }

    #endregion

    #region fields

    protected object syncObject = new object();
    protected Dictionary<string, PluginContainer> plugins = new Dictionary<string, PluginContainer>();
    protected Dictionary<string, CrossDomainObject> notifierContexts = new Dictionary<string, CrossDomainObject>();
    protected TModel model;

    private string path;
    private List<PluginInfo> infos;

    private Thread processThread;
    private ManualResetEvent processThreadEvent;

    private bool disposed;

    #endregion

    #region Initialization

    protected PluginManager(string pluginsPath)
    {
      path = pluginsPath;
    }

    public void Dispose()
    {
      if (disposed)
        return;

      disposed = true;

      var containers = plugins.Values.ToList();
      foreach (var container in containers)
        UnloadPlugin(container);

      processThreadEvent.Set();
      processThread.Join();
      processThread = null;
    }

    #endregion

    #region overridable

    protected virtual void OnPluginLoaded(PluginContainer loaded)
    {
      var context = loaded.Plugin.NotifierContext;
      if (context != null)
        notifierContexts.Add(loaded.Plugin.Name, context);
    }

    protected virtual void OnPluginUnlodaing(PluginContainer unloading) 
    {
      notifierContexts.Remove(unloading.Plugin.Name);
    }

    protected virtual void OnError(string message, Exception e) { }

    protected virtual void Process() { }

    #endregion

    #region load

    internal void LoadPlugins(string[] excludedPlugins)
    {
      try
      {
        model = new TModel();

        var libs = FindLibraries(path);
        var loader = new PluginInfoLoader();
        infos = loader.LoadFrom(typeof(TPlugin).FullName, libs);
        if (infos != null)
          foreach (var info in infos)
            LoadPlugin(info, excludedPlugins);

        processThreadEvent = new ManualResetEvent(false);
        processThread = new Thread(ProcessThreadHandler);
        processThread.IsBackground = true;
        processThread.Start();
      }
      catch (Exception e)
      {
        OnError("load plugins failed", e);
      }
    }

    public void LoadPlugin(string name)
    {
      var info = infos.Find(pi => pi.Name == name);
      if (info == null)
        return;

      LoadPlugin(info, null);
    }

    private void LoadPlugin(PluginInfo info, string[] excludedPlugins)
    {
      lock (syncObject)
      {
        var domainSetup = new AppDomainSetup();
        domainSetup.ApplicationBase = AppDomain.CurrentDomain.BaseDirectory;
        domainSetup.PrivateBinPath = "plugins;bin";

        var permissions = new PermissionSet(PermissionState.None);
        permissions.AddPermission(new UIPermission(PermissionState.Unrestricted));
        permissions.AddPermission(new ReflectionPermission(PermissionState.Unrestricted));

        permissions.AddPermission(new SecurityPermission(
          SecurityPermissionFlag.Execution |
          SecurityPermissionFlag.UnmanagedCode |
          SecurityPermissionFlag.SerializationFormatter |
          SecurityPermissionFlag.Assertion));

        permissions.AddPermission(new FileIOPermission(
          FileIOPermissionAccess.PathDiscovery |
          FileIOPermissionAccess.Write |
          FileIOPermissionAccess.Read,
          AppDomain.CurrentDomain.BaseDirectory));

        var domain = AppDomain.CreateDomain(string.Format("Plugin Domain [{0}]", Path.GetFileNameWithoutExtension(info.AssemblyPath)), null, domainSetup, permissions);
        var pluginName = string.Empty;
        try
        {
          var plugin = (TPlugin)domain.CreateInstanceFromAndUnwrap(info.AssemblyPath, info.TypeName);
          info.Name = plugin.Name;
          pluginName = plugin.Name;

          if (plugins.ContainsKey(pluginName) || (excludedPlugins != null && excludedPlugins.Contains(pluginName)))
          {
            AppDomain.Unload(domain);
            return;
          }

          plugin.Initialize(model);

          var container = new PluginContainer(domain, plugin);
          plugins.Add(pluginName, container);
          info.Loaded = true;

          OnPluginLoaded(container);
        }
        catch (Exception e)
        {
          OnError(string.Format("plugin failed: {0}", pluginName), e);

          if (UnloadPlugin(pluginName))
            return;
                    
          AppDomain.Unload(domain);
        }
      }
    }

    #endregion

    #region unload

    public bool UnloadPlugin(string name)
    {
      lock (syncObject)
      {
        PluginContainer container;
        if (plugins.TryGetValue(name, out container))
          return UnloadPlugin(container);

        return false;
      }
    }

    protected bool UnloadPlugin(Plugin<TModel> plugin)
    {
      lock (syncObject)
      {
        var containers = plugins.Values.ToList();
        foreach (var container in containers)
        {
          if (ReferenceEquals(container.Plugin, plugin))
            return UnloadPlugin(container);
        }

        return false;
      }
    }

    protected bool UnloadPlugin(PluginContainer container)
    {
      lock (syncObject)
      {
        OnPluginUnlodaing(container);

        plugins.Remove(container.Plugin.Name);
        AppDomain.Unload(container.Domain);
        return true;
      }
    }

    #endregion unload

    public bool IsLoaded(string name)
    {
      return plugins.ContainsKey(name);
    }

    public string[] GetPlugins()
    {
      return infos.Select(pi => pi.Name).ToArray();
    }

    internal IEnumerable GetNotifierContexts()
    {
      return notifierContexts.Values;
    }

    protected void ProcessThreadHandler()
    {
      while (true)
      {
        if (processThreadEvent.WaitOne(ProcessTimeout))
          return;

        lock (syncObject)
        {
          foreach (var container in plugins.Values)
            container.Plugin.Process();

          foreach (var context in notifierContexts.Values)
            context.Process();
        }

        Process();
      }
    }

    private static string[] FindLibraries(string path)
    {
      return Directory.GetFiles(path).Where(f => f.Contains(".dll")).ToArray();
    }
  }
}
