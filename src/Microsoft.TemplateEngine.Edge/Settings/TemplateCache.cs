﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if !NET45
using System.Runtime.Loader;
#endif
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Mount;
using Microsoft.TemplateEngine.Edge.Mount.FileSystem;
using Microsoft.TemplateEngine.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.TemplateEngine.Edge.Settings
{
    public class TemplateCache
    {
        private IDictionary<string, ITemplate> _templateMemoryCache = new Dictionary<string, ITemplate>();

        // locale -> identity -> locator
        private readonly IDictionary<string, IDictionary<string, ILocalizationLocator>> _localizationMemoryCache = new Dictionary<string, IDictionary<string, ILocalizationLocator>>();
        private readonly IEngineEnvironmentSettings _environmentSettings;
        private readonly Paths _paths;

        public TemplateCache(IEngineEnvironmentSettings environmentSettings)
        {
            _environmentSettings = environmentSettings;
            _paths = new Paths(environmentSettings);
            TemplateInfo = new List<TemplateInfo>();
        }

        public TemplateCache(IEngineEnvironmentSettings environmentSettings, JObject parsed)
            : this(environmentSettings)
        {
            if (parsed.TryGetValue("TemplateInfo", StringComparison.OrdinalIgnoreCase, out JToken templateInfoToken))
            {
                if (templateInfoToken is JArray arr)
                {
                    foreach (JToken entry in arr)
                    {
                        if (entry != null && entry.Type == JTokenType.Object)
                        {
                            TemplateInfo.Add(new TemplateInfo((JObject)entry));
                        }
                    }
                }
            }
        }

        [JsonProperty]
        public List<TemplateInfo> TemplateInfo { get; set; }

        public void Scan(IReadOnlyList<string> templateRoots)
        {
            foreach (string templateDir in templateRoots)
            {
                Scan(templateDir);
            }
        }

        // reads all the templates and langpacks for the current dir.
        // stores info about them in members.
        // can't correctly write locale cache(s) until all of both are read.
        public void Scan(string templateDir)
        {
            string searchTarget = Path.Combine(_environmentSettings.Host.FileSystem.GetCurrentDirectory(), templateDir.Trim());
            List<string> matches = _environmentSettings.Host.FileSystem.EnumerateFileSystemEntries(Path.GetDirectoryName(searchTarget), Path.GetFileName(searchTarget), SearchOption.TopDirectoryOnly).ToList();

            if (matches.Count == 1)
            {
                templateDir = matches[0];
            }
            else
            {
                foreach(string match in matches)
                {
                    Scan(match);
                }

                return;
            }

            if (_environmentSettings.SettingsLoader.TryGetMountPointFromPlace(searchTarget, out IMountPoint existingMountPoint))
            {
                ScanMountPointForTemplatesAndLangpacks(existingMountPoint, templateDir);
            }
            else
            {
                foreach (IMountPointFactory factory in _environmentSettings.SettingsLoader.Components.OfType<IMountPointFactory>().ToList())
                {
                    if (factory.TryMount(_environmentSettings, null, templateDir, out IMountPoint mountPoint))
                    {
                        // TODO: consider not adding the mount point if there is nothing to install.
                        // It'd require choosing to not write it upstream from here, which might be better anyway.
                        // "nothing to install" could have a couple different meanings:
                        // 1) no templates, and no langpacks were found.
                        // 2) only langpacks were found, but they aren't for any existing templates - but we won't know that at this point.
                        _environmentSettings.SettingsLoader.AddMountPoint(mountPoint);
                        ScanMountPointForTemplatesAndLangpacks(mountPoint, templateDir);
                    }
                }
            }
        }

        private void ScanMountPointForTemplatesAndLangpacks(IMountPoint mountPoint, string templateDir)
        {
            ScanForComponents(mountPoint, templateDir);

            foreach (IGenerator generator in _environmentSettings.SettingsLoader.Components.OfType<IGenerator>())
            {
                IEnumerable<ITemplate> templateList = generator.GetTemplatesAndLangpacksFromDir(mountPoint, out IList<ILocalizationLocator> localizationInfo);

                foreach (ILocalizationLocator locator in localizationInfo)
                {
                    AddLocalizationToMemoryCache(locator);
                }

                foreach (ITemplate template in templateList)
                {
                    AddTemplateToMemoryCache(template);
                }
            }
        }

        private void ScanForComponents(IMountPoint mountPoint, string templateDir)
        {
            if (mountPoint.Root.EnumerateFiles("*.dll", SearchOption.AllDirectories).Any())
            {
                string diskPath = templateDir;
                if (mountPoint.Info.MountPointFactoryId != FileSystemMountPointFactory.FactoryId)
                {
                    string path = Path.Combine(_paths.User.Content, Path.GetFileName(templateDir));

                    try
                    {
                        mountPoint.Root.CopyTo(path);
                    }
                    catch (IOException)
                    {
                        return;
                    }

                    diskPath = path;
                }

                foreach (KeyValuePair<string, Assembly> asm in AssemblyLoader.LoadAllAssemblies(_paths, out IEnumerable<string> failures))
                {
                    try
                    {
                        foreach (Type type in asm.Value.GetTypes())
                        {
                            _environmentSettings.SettingsLoader.Components.Register(type);
                        }

                        _environmentSettings.SettingsLoader.AddProbingPath(Path.GetDirectoryName(asm.Key));
                    }
                    catch
                    {
                    }
                }
            }
        }

        public List<TemplateInfo> LoadTemplateCacheForLocale(string locale)
        {
            string cacheContent = _paths.ReadAllText(_paths.User.ExplicitLocaleTemplateCacheFile(locale), "{}");
            JObject parsed = JObject.Parse(cacheContent);
            List<TemplateInfo> templates = new List<TemplateInfo>();

            if (parsed.TryGetValue("TemplateInfo", StringComparison.OrdinalIgnoreCase, out JToken templateInfoToken))
            {
                if (templateInfoToken is JArray arr)
                {
                    foreach (JToken entry in arr)
                    {
                        if (entry != null && entry.Type == JTokenType.Object)
                        {
                            templates.Add(new TemplateInfo((JObject)entry));
                        }
                    }
                }
            }

            return templates;
        }

        // Writes template caches for each of the following:
        //  - current locale
        //  - cultures for which new langpacks are installed
        //  - other locales with existing caches are regenerated.
        //  - neutral locale
        public void WriteTemplateCaches()
        {
            string currentLocale = _environmentSettings.Host.Locale;
            HashSet<string> localesWritten = new HashSet<string>();

            // If the current locale exists, always write it.
            if (! string.IsNullOrEmpty(currentLocale))
            {
                WriteTemplateCacheForLocale(currentLocale);
                localesWritten.Add(currentLocale);
            }

            // write caches for any locales which had new langpacks installed
            foreach (string langpackLocale in _localizationMemoryCache.Keys)
            {
                WriteTemplateCacheForLocale(langpackLocale);
                localesWritten.Add(langpackLocale);
            }

            // read the cache dir for other locale caches, and re-write them.
            // there may be new templates to add to them.
            string fileSearchPattern = "*." + _paths.User.TemplateCacheFileBaseName;
            foreach (string fullFilename in _paths.EnumerateFiles(_paths.User.BaseDir, fileSearchPattern, SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileName(fullFilename);
                string[] fileParts = filename.Split(new char[] { '.' }, 2);
                string fileLocale = fileParts[0];

                if (!string.IsNullOrEmpty(fileLocale) &&
                    (fileParts[1] == _paths.User.TemplateCacheFileBaseName)
                    && !localesWritten.Contains(fileLocale))
                {
                    WriteTemplateCacheForLocale(fileLocale);
                    localesWritten.Add(fileLocale);
                }
            }

            // always write the culture neutral cache
            // It must be written last because when a cache for a culture is first created, it's based on the
            // culture neutral cache, plus newly registered templates.
            // If the culture neutral cache is updated before the new cache is first written,
            // the new cache will have duplicate values.
            //
            // being last may not matter anymore due to changes after the comment was written.
            WriteTemplateCacheForLocale(null);
        }

        public void DeleteAllLocaleCacheFiles()
        {
            string fileSearchPattern = "*." + _paths.User.TemplateCacheFileBaseName;
            foreach (string fullFilename in _paths.EnumerateFiles(_paths.User.BaseDir, fileSearchPattern, SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileName(fullFilename);
                string[] fileParts = filename.Split(new char[] { '.' }, 2);
                string fileLocale = fileParts[0];

                if (!string.IsNullOrEmpty(fileLocale) &&
                    (fileParts[1] == _paths.User.TemplateCacheFileBaseName))
                {
                    _paths.Delete(fullFilename);
                }
            }

            _paths.Delete(_paths.User.CultureNeutralTemplateCacheFile);
        }

        private void WriteTemplateCacheForLocale(string locale)
        {
            List<TemplateInfo> existingTemplatesForLocale = LoadTemplateCacheForLocale(locale);
            IDictionary<string, ILocalizationLocator> existingLocatorsForLocale;

            if (existingTemplatesForLocale.Count == 0)
            {   // the cache for this locale didn't exist previously. Start with the neutral locale as if it were the existing (no locales)
                existingTemplatesForLocale = LoadTemplateCacheForLocale(null);
                existingLocatorsForLocale = new Dictionary<string, ILocalizationLocator>();
            }
            else
            {
                existingLocatorsForLocale = GetLocalizationsFromTemplates(existingTemplatesForLocale, locale);
            }

            HashSet<string> foundTemplates = new HashSet<string>();
            List<ITemplateInfo> mergedTemplateList = new List<ITemplateInfo>();

            // These are from langpacks being installed... identity -> locator
            if (string.IsNullOrEmpty(locale)
                || !_localizationMemoryCache.TryGetValue(locale, out IDictionary<string, ILocalizationLocator> newLocatorsForLocale))
            {
                newLocatorsForLocale = new Dictionary<string, ILocalizationLocator>();
            }

            foreach (ITemplate newTemplate in _templateMemoryCache.Values)
            {
                ILocalizationLocator locatorForTemplate = GetPreferredLocatorForTemplate(newTemplate.Identity, existingLocatorsForLocale, newLocatorsForLocale);
                TemplateInfo localizedTemplate = LocalizeTemplate(newTemplate, locatorForTemplate);
                mergedTemplateList.Add(localizedTemplate);
                foundTemplates.Add(newTemplate.Identity);
            }

            foreach (TemplateInfo existingTemplate in existingTemplatesForLocale)
            {
                if (!foundTemplates.Contains(existingTemplate.Identity))
                {
                    ILocalizationLocator locatorForTemplate = GetPreferredLocatorForTemplate(existingTemplate.Identity, existingLocatorsForLocale, newLocatorsForLocale);
                    TemplateInfo localizedTemplate = LocalizeTemplate(existingTemplate, locatorForTemplate);
                    mergedTemplateList.Add(localizedTemplate);
                    foundTemplates.Add(existingTemplate.Identity);
                }
            }

            bool isCurrentLocale = string.IsNullOrEmpty(locale)
                && string.IsNullOrEmpty(_environmentSettings.Host.Locale)
                || (locale == _environmentSettings.Host.Locale);
            _environmentSettings.SettingsLoader.WriteTemplateCache(mergedTemplateList, locale, isCurrentLocale);
        }

        // find the best locator (if any). New is preferred over old
        private ILocalizationLocator GetPreferredLocatorForTemplate(string identity, IDictionary<string, ILocalizationLocator> existingLocatorsForLocale, IDictionary<string, ILocalizationLocator> newLocatorsForLocale)
        {
            if (!newLocatorsForLocale.TryGetValue(identity, out ILocalizationLocator locatorForTemplate))
            {
                existingLocatorsForLocale.TryGetValue(identity, out locatorForTemplate);
            }

            return locatorForTemplate;
        }

        private TemplateInfo LocalizeTemplate(ITemplateInfo template, ILocalizationLocator localizationInfo)
        {
            TemplateInfo localizedTemplate = new TemplateInfo
            {
                GeneratorId = template.GeneratorId,
                ConfigPlace = template.ConfigPlace,
                ConfigMountPointId = template.ConfigMountPointId,
                Name = localizationInfo?.Name ?? template.Name,
                Tags = template.Tags,
                ShortName = template.ShortName,
                Classifications = template.Classifications,
                Author = localizationInfo?.Author ?? template.Author,
                Description = localizationInfo?.Description ?? template.Description,
                GroupIdentity = template.GroupIdentity,
                Identity = template.Identity,
                DefaultName = template.DefaultName,
                LocaleConfigPlace = localizationInfo?.ConfigPlace ?? null,
                LocaleConfigMountPointId = localizationInfo?.MountPointId ?? Guid.Empty
            };

            return localizedTemplate;
        }

        // return dict is: Identity -> locator
        private IDictionary<string, ILocalizationLocator> GetLocalizationsFromTemplates(IList<TemplateInfo> templateList, string locale)
        {
            IDictionary<string, ILocalizationLocator> locatorLookup = new Dictionary<string, ILocalizationLocator>();

            foreach (TemplateInfo template in templateList)
            {
                if (template.LocaleConfigMountPointId == null
                    || template.LocaleConfigMountPointId == Guid.Empty)
                {   // Indicates an unlocalized entry in the locale specific template cache.
                    continue;
                }

                ILocalizationLocator locator = new LocalizationLocator()
                {
                    Locale = locale,
                    MountPointId = template.LocaleConfigMountPointId,
                    ConfigPlace = template.LocaleConfigPlace,
                    Identity = template.Identity,
                    Author = template.Author,
                    Name = template.Name,
                    Description = template.Description
                };

                locatorLookup.Add(locator.Identity, locator);
            }

            return locatorLookup;
        }

        // Adds the template to the memory cache, keyed on identity.
        // If the identity is the same as an existing one, it's overwritten.
        // (last in wins)
        private void AddTemplateToMemoryCache(ITemplate template)
        {
            _templateMemoryCache[template.Identity] = template;
        }

        private void AddLocalizationToMemoryCache(ILocalizationLocator locator)
        {
            if (!_localizationMemoryCache.TryGetValue(locator.Locale, out IDictionary<string, ILocalizationLocator> localeLocators))
            {
                localeLocators = new Dictionary<string, ILocalizationLocator>();
                _localizationMemoryCache.Add(locator.Locale, localeLocators);
            }

            localeLocators[locator.Identity] = locator;
        }
    }
}
