﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit.Internal;
using Xunit.Runner.Common;

namespace Xunit.Runner.SystemConsole
{
	public class CommandLine
	{
		readonly Stack<string> arguments = new Stack<string>();
		XunitProject? project;
		readonly List<string> unknownOptions = new List<string>();

		protected CommandLine(
			string[] args,
			Predicate<string>? fileExists = null)
		{
			try
			{
				fileExists ??= File.Exists;

				for (var i = args.Length - 1; i >= 0; i--)
					arguments.Push(args[i]);

				Project = Parse(fileExists);
			}
			catch (Exception ex)
			{
				ParseFault = ex;
			}
		}

		public Exception? ParseFault { get; protected set; }

		public XunitProject Project
		{
			get => project ?? throw new InvalidOperationException($"Attempted to get {nameof(Project)} on an uninitialized '{GetType().FullName}' object");
			protected set => project = Guard.ArgumentNotNull(nameof(Project), value);
		}

		public IRunnerReporter ChooseReporter(IReadOnlyList<IRunnerReporter> reporters)
		{
			var result = default(IRunnerReporter);

			foreach (var unknownOption in unknownOptions)
			{
				var reporter = reporters.FirstOrDefault(r => r.RunnerSwitch == unknownOption) ?? throw new ArgumentException($"unknown option: -{unknownOption}");

				if (result != null)
					throw new ArgumentException("only one reporter is allowed");

				result = reporter;
			}

			if (!Project.Configuration.NoAutoReportersOrDefault)
				result = reporters.FirstOrDefault(r => r.IsEnvironmentallyEnabled) ?? result;

			return result ?? new DefaultRunnerReporter();
		}

		protected virtual string GetFullPath(string fileName) => Path.GetFullPath(fileName);

		XunitProject GetProjectFile(List<(string assemblyFileName, string? configFileName)> assemblies)
		{
			var result = new XunitProject();

			foreach (var assembly in assemblies)
			{
				var targetFramework = AssemblyUtility.GetTargetFramework(assembly.assemblyFileName);
				var projectAssembly = new XunitProjectAssembly(result)
				{
					AssemblyFilename = GetFullPath(assembly.assemblyFileName),
					ConfigFilename = assembly.Item2 != null ? GetFullPath(assembly.configFileName) : null,
					TargetFramework = targetFramework
				};

				ConfigReader.Load(projectAssembly.Configuration, projectAssembly.AssemblyFilename, projectAssembly.ConfigFilename);
				result.Add(projectAssembly);
			}

			return result;
		}

		static void GuardNoOptionValue(KeyValuePair<string, string?> option)
		{
			if (option.Value != null)
				throw new ArgumentException($"error: unknown command line option: {option.Value}");
		}

		static bool IsConfigFile(string fileName)
		{
			return fileName.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
				|| fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
		}

		public static CommandLine Parse(params string[] args)
			=> new CommandLine(args);

		protected XunitProject Parse(Predicate<string> fileExists)
		{
			var assemblies = new List<(string assemblyFileName, string? configFileName)>();

			while (arguments.Count > 0)
			{
				if (arguments.Peek().StartsWith("-", StringComparison.Ordinal))
					break;

				var assemblyFile = arguments.Pop();
				if (IsConfigFile(assemblyFile))
					throw new ArgumentException($"expecting assembly, got config file: {assemblyFile}");
				if (!fileExists(assemblyFile))
					throw new ArgumentException($"file not found: {assemblyFile}");

				string? configFile = null;
				if (arguments.Count > 0)
				{
					var value = arguments.Peek();
					if (!value.StartsWith("-", StringComparison.Ordinal) && IsConfigFile(value))
					{
						configFile = arguments.Pop();
						if (!fileExists(configFile))
							throw new ArgumentException($"config file not found: {configFile}");
					}
				}

				assemblies.Add((assemblyFile, configFile));
			}

			var project = GetProjectFile(assemblies);

			while (arguments.Count > 0)
			{
				var option = PopOption(arguments);
				var optionName = option.Key.ToLowerInvariant();

				if (!optionName.StartsWith("-", StringComparison.Ordinal))
					throw new ArgumentException($"unknown command line option: {option.Key}");

				optionName = optionName.Substring(1);

				if (optionName == "nologo")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoLogo = true;
				}
				else if (optionName == "failskips")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.FailSkips = true;
				}
				else if (optionName == "stoponfail")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.StopOnFail = true;
				}
				else if (optionName == "nocolor")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoColor = true;
				}
				else if (optionName == "noappdomain")    // Here for historical reasons
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.AppDomain = AppDomainSupport.Denied;
				}
				else if (optionName == "noautoreporters")
				{
					GuardNoOptionValue(option);
					project.Configuration.NoAutoReporters = true;
				}
				else if (optionName == "pause")
				{
					GuardNoOptionValue(option);
					project.Configuration.Pause = true;
				}
				else if (optionName == "preenumeratetheories")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.PreEnumerateTheories = true;
				}
				else if (optionName == "debug")
				{
					GuardNoOptionValue(option);
					project.Configuration.Debug = true;
				}
				else if (optionName == "serialize")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.IncludeSerialization = true;
				}
				else if (optionName == "wait")
				{
					GuardNoOptionValue(option);
					project.Configuration.Wait = true;
				}
				else if (optionName == "diagnostics")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.DiagnosticMessages = true;
				}
				else if (optionName == "internaldiagnostics")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.InternalDiagnosticMessages = true;
				}
				else if (optionName == "appdomains")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -appdomains");

					var appDomainSupport = option.Value switch
					{
						"required" => AppDomainSupport.Required,
						"denied" => AppDomainSupport.Denied,
						_ => throw new ArgumentException("incorrect argument value for -appdomains (must be 'required' or 'denied')"),
					};

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.AppDomain = appDomainSupport;
				}
				else if (optionName == "maxthreads")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -maxthreads");

					int? maxParallelThreads = null;

					switch (option.Value)
					{
						case "default":
							maxParallelThreads = 0;
							break;

						case "unlimited":
							maxParallelThreads = -1;
							break;

						default:
							int threadValue;
							if (!int.TryParse(option.Value, out threadValue) || threadValue < 1)
								throw new ArgumentException("incorrect argument value for -maxthreads (must be 'default', 'unlimited', or a positive number)");

							maxParallelThreads = threadValue;
							break;
					}

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.MaxParallelThreads = maxParallelThreads;
				}
				else if (optionName == "parallel")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -parallel");

					if (!Enum.TryParse(option.Value, ignoreCase: true, out ParallelismOption parallelismOption))
						throw new ArgumentException("incorrect argument value for -parallel");

					var (parallelizeAssemblies, parallelizeTestCollections) = parallelismOption switch
					{
						ParallelismOption.all => (true, true),
						ParallelismOption.assemblies => (true, false),
						ParallelismOption.collections => (false, true),
						_ => (false, false)
					};

					foreach (var assembly in project.Assemblies)
					{
						assembly.Configuration.ParallelizeAssembly = parallelizeAssemblies;
						assembly.Configuration.ParallelizeTestCollections = parallelizeTestCollections;
					}
				}
				else if (optionName == "noshadow")
				{
					GuardNoOptionValue(option);
					foreach (var assembly in project.Assemblies)
						assembly.Configuration.ShadowCopy = false;
				}
				else if (optionName == "trait")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -trait");

					var pieces = option.Value.Split('=');
					if (pieces.Length != 2 || string.IsNullOrEmpty(pieces[0]) || string.IsNullOrEmpty(pieces[1]))
						throw new ArgumentException("incorrect argument format for -trait (should be \"name=value\")");

					var name = pieces[0];
					var value = pieces[1];

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.IncludedTraits.Add(name, value);
				}
				else if (optionName == "notrait")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -notrait");

					var pieces = option.Value.Split('=');
					if (pieces.Length != 2 || string.IsNullOrEmpty(pieces[0]) || string.IsNullOrEmpty(pieces[1]))
						throw new ArgumentException("incorrect argument format for -notrait (should be \"name=value\")");

					var name = pieces[0];
					var value = pieces[1];

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.ExcludedTraits.Add(name, value);
				}
				else if (optionName == "class")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -class");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.IncludedClasses.Add(option.Value);
				}
				else if (optionName == "noclass")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -noclass");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.ExcludedClasses.Add(option.Value);
				}
				else if (optionName == "method")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -method");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.IncludedMethods.Add(option.Value);
				}
				else if (optionName == "nomethod")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -nomethod");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.ExcludedMethods.Add(option.Value);
				}
				else if (optionName == "namespace")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -namespace");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.IncludedNamespaces.Add(option.Value);
				}
				else if (optionName == "nonamespace")
				{
					if (option.Value == null)
						throw new ArgumentException("missing argument for -nonamespace");

					foreach (var assembly in project.Assemblies)
						assembly.Configuration.Filters.ExcludedNamespaces.Add(option.Value);
				}
				else
				{
					// Might be a result output file...
					if (TransformFactory.AvailableTransforms.Any(t => t.ID.Equals(optionName, StringComparison.OrdinalIgnoreCase)))
					{
						if (option.Value == null)
							throw new ArgumentException($"missing filename for {option.Key}");

						EnsurePathExists(option.Value);

						project.Configuration.Output.Add(optionName, option.Value);
					}
					// ...or it might be a reporter (we won't know until later)
					else
					{
						GuardNoOptionValue(option);
						unknownOptions.Add(optionName);
					}
				}
			}

			return project;
		}

		static KeyValuePair<string, string?> PopOption(Stack<string> arguments)
		{
			var option = arguments.Pop();
			string? value = null;

			if (arguments.Count > 0 && !arguments.Peek().StartsWith("-", StringComparison.Ordinal))
				value = arguments.Pop();

			return new KeyValuePair<string, string?>(option, value);
		}

		static void EnsurePathExists(string path)
		{
			var directory = Path.GetDirectoryName(path);

			if (string.IsNullOrEmpty(directory))
				return;

			Directory.CreateDirectory(directory);
		}
	}
}
