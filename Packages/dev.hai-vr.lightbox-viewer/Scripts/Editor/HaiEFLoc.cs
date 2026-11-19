/*
MIT License

Copyright (c) 2026 Haï~ (@vr_hai github.com/hai-vr)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    // HEFLoc V0.1.9005
    //
    /// <summary>
    /// A self-contained localization tool, which will be copied into multiple of my packages.
    /// </summary>
    public class HaiEFLoc
    {
        private readonly string _root;

        private const string LocalizationPrefs = "HVR.EF.Localization.language";
        private const string MissingLocalizationKeyPrefix = "__";
        private const string LanguageLabel = "Language";

        private LocalizationData _loaded;
        private readonly List<LocalizationData> _availableLanguages = new();
        private LocalizationData _defaultLanguage;
        
        private int _selected;
        private readonly GUIContent[] _selector;
        private string _attemptedToLoad;

        public HaiEFLoc(string root, string folder)
        {
            _root = root;

            var assets = AssetDatabase.FindAssets("", new[] { folder });
            var localizationPaths = assets
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".json"))
                .ToList();

            var requestedLanguage = EditorPrefs.GetString(LocalizationPrefs, "en");

            foreach (var localizationPath in localizationPaths)
            {
                var parsed = TryParsePathOrNull(localizationPath);
                if (parsed != null)
                {
                    if (parsed.Language == "en")
                    {
                        _defaultLanguage = parsed;
                        _availableLanguages.Insert(0, parsed);
                    }
                    else
                    {
                        _availableLanguages.Add(parsed);
                    }
                }
            }

            if (_defaultLanguage == null)
            {
                Debug.LogError("(HaiEFLoc) No default language found.");
            }

            _selector = _availableLanguages.Select(data => new GUIContent(data.CleanName)).ToArray();
            TryApplyRequestedLanguage(requestedLanguage);
        }

        public void RefreshIfNecessary()
        {
            var requestedLanguage = EditorPrefs.GetString(LocalizationPrefs, "en");
            if (requestedLanguage != _attemptedToLoad)
            {
                TryApplyRequestedLanguage(requestedLanguage);
            }
        }

        private void TryApplyRequestedLanguage(string requestedLanguage)
        {
            // _attemptedToLoad is not necessarily the language that will be loaded; this happens when the loop below fails to resolve.
            _attemptedToLoad = requestedLanguage;
            
            for (var index = 0; index < _availableLanguages.Count; index++)
            {
                var localizationData = _availableLanguages[index];
                if (localizationData.Language == requestedLanguage)
                {
                    _loaded = localizationData;
                    _selected = index;
                    return;
                }
            }

            _loaded = _availableLanguages[0];
            _selected = 0;
        }

        private LocalizationData TryParsePathOrNull(string assetPath)
        {
            try
            {
                var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
                var all = JObject.Parse(textAsset.text);
                var meta = all["_meta"]!.ToObject<JObject>();
                
                var ours = all[_root]!.ToObject<JObject>();

                var language = meta["language"]!.Value<string>();
                var name = meta["name"]!.Value<string>();
                var variant = meta["variant"]!.Value<string>();

                Dictionary<string, string> data = new();
                foreach (var (key, value) in ours["phrases"]!.ToObject<JObject>())
                {
                    data.Add($"phrases.{key}", value!.Value<string>());
                }
                foreach (var (enumKey, enumValues) in ours["enums"]!.ToObject<JObject>())
                {
                    foreach (var (key, value) in enumValues!.ToObject<JObject>())
                    {
                        data.Add($"enums.{enumKey}.{key}", value!.Value<string>());
                    }
                }

                return new LocalizationData(language, name, variant, data);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return null;
            }
        }

        public void PropertyField(string localizationKey, SerializedProperty property)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(Text(localizationKey)));
        }

        public void EnumPropertyField<TEnum>(string localizationKey, SerializedProperty property)
        {
            var newValue = EditorGUILayout.Popup(new GUIContent(Text(localizationKey)), property.intValue, property.enumNames.Select(
                (enumName, i) => LocalizeEnumName(typeof(TEnum).FullName, enumName)).ToArray());
            if (newValue != property.intValue)
            {
                property.intValue = newValue;
            }
        }
        
        public void LabelField(string localizationKey) => EditorGUILayout.LabelField(Text(localizationKey));
        public void LabelField(string localizationKey, GUIStyle style) => EditorGUILayout.LabelField(Text(localizationKey), style);
        public void LabelField(string localizationKey, GUIStyle style, params GUILayoutOption[] options) => EditorGUILayout.LabelField(Text(localizationKey), style, options);
        public void LabelField(string localizationKey, params GUILayoutOption[] options) => EditorGUILayout.LabelField(Text(localizationKey), options);
        public void HelpBox(string localizationKey, MessageType messageType) => EditorGUILayout.HelpBox(Text(localizationKey), messageType);
        public bool Button(string localizationKey) => GUILayout.Button(Text(localizationKey));

        public string Text(string localizationKey)
        {
            return DoLocalize($"phrases.{localizationKey}");
        }

        public string Format(string localizationKey, params object[] format)
        {
            return string.Format(DoLocalize($"phrases.{localizationKey}"), format);
        }

        private string LocalizeEnumName(string enumType, string enumValue)
        {
            return DoLocalize($"enums.{enumType}.{enumValue}");
        }

        private string DoLocalize(string actualKey)
        {
            if (_loaded.Data.TryGetValue(actualKey, out var value)) return value;
            if (_defaultLanguage.Data.TryGetValue(actualKey, out value)) return MissingLocalizationKeyPrefix + value;
            return MissingLocalizationKeyPrefix + actualKey;
        }

        public void Selector(Func<HaiEFLoc> remakeLocalization)
        {
            EditorGUILayout.Separator();
            EditorGUILayout.BeginHorizontal();
            var newSelected = EditorGUILayout.Popup(new GUIContent(LanguageLabel), _selected, _selector);
            if (newSelected != _selected)
            {
                _selected = newSelected;
                var requestedLanguage = _availableLanguages[_selected].Language;
                TryApplyRequestedLanguage(requestedLanguage);
                
                EditorPrefs.SetString(LocalizationPrefs, requestedLanguage);
            }
            if (GUILayout.Button(EditorGUIUtility.IconContent("d_Refresh"), GUILayout.Width(20)))
            {
                remakeLocalization.Invoke();
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    internal class LocalizationData
    {
        public string Language { get; }
        public string Name { get; }
        public string Localizer { get; }
        public Dictionary<string, string> Data { get; }
        public string CleanName { get; }

        public LocalizationData(string language, string name, string localizer, Dictionary<string, string> data)
        {
            Language = language;
            Name = name;
            Localizer = localizer;
            Data = data;
            CleanName = localizer == "" ? name : $"{name} ({localizer})";
        }
    }
}