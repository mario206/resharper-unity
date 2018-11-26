using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Yaml.Psi.Caches;
using JetBrains.ReSharper.Plugins.Unity.Yaml.Psi.Modules;
using JetBrains.ReSharper.Plugins.Yaml.Psi;
using JetBrains.ReSharper.Plugins.Yaml.Psi.Tree;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches.Persistence;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using JetBrains.Util.dataStructures;
using Lex;

namespace JetBrains.ReSharper.Plugins.Unity.Yaml.Psi
{
    // TODO [Krasnotsvetov] all comments is out-of-date and incorrect
    public static class UnityObjectPsiUtil
    {
        [NotNull]
        public static string GetComponentName([NotNull] IYamlDocument componentDocument)
        {
            var name = componentDocument.GetUnityObjectPropertyValue("m_Name").AsString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var scriptDocument = componentDocument.GetUnityObjectDocumentFromFileIDProperty("m_Script");
            name = scriptDocument.GetUnityObjectPropertyValue("m_Name").AsString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var fileID = componentDocument.GetUnityObjectPropertyValue("m_Script").AsFileID();
            if (fileID != null && fileID.IsExternal && fileID.IsMonoScript)
            {
                var typeElement = GetTypeElementFromScriptAssetGuid(componentDocument.GetSolution(), fileID.guid);
                if (typeElement != null)
                {
                    // TODO: Format like in Unity, by splitting the camel humps
                    return typeElement.ShortName + " (Script)";
                }
            }

            return scriptDocument.GetUnityObjectTypeFromRootNode()
                   ?? componentDocument.GetUnityObjectTypeFromRootNode()
                   ?? "Component";
        }

        
        public static void ProcessToRoot([CanBeNull] IYamlDocument startGameObject, Action<IYamlDocument, IBlockMappingNode> selector)
        {
            if (startGameObject == null)
                return;
            
            var solution = startGameObject.GetSolution();
            var yamlFileCache = solution.GetComponent<MetaFileGuidCache>();
            var externalFilesModuleFactory = solution.GetComponent<UnityExternalFilesModuleFactory>();
            // Common method for process prefab/scene. When we processing scene, we do not have modification. 
            // If we process prefab, the root prefab do not have modification too
            ProcessPrefabFromToRoot(yamlFileCache, externalFilesModuleFactory, startGameObject, null, selector);
        }

        private static void ProcessPrefabFromToRoot(MetaFileGuidCache yamlFileCache, UnityExternalFilesModuleFactory externalFilesModuleFactory,
            IYamlDocument startGameObject, IBlockMappingNode modifications, Action<IYamlDocument, IBlockMappingNode> selector)
        {
            if (startGameObject == null)
                return;
            var currentGameObject = startGameObject;
            while (currentGameObject != null)
            {
                var correspondingId = currentGameObject.GetUnityObjectPropertyValue("m_CorrespondingSourceObject")?.AsFileID();
                string prefabInstanceId = currentGameObject.GetUnityObjectPropertyValue("m_PrefabInstance")?.AsFileID()?.fileID;
                if (correspondingId != null && correspondingId != FileID.Null)
                {
                    // This should never happen, but data can be corrupted
                    if (prefabInstanceId == null || prefabInstanceId.Equals("0"))
                        return;

                    var file = (IYamlFile) currentGameObject.GetContainingFile();
                    var prefabInstance = file.FindDocumentByAnchor(prefabInstanceId);
                    
                    var prefabSourceFile = yamlFileCache.GetAssetFilePathsFromGuid(correspondingId.guid);
                    if (prefabSourceFile.Count > 1 || prefabSourceFile.Count == 0) 
                        return;

                    // Is prefab file committed???
                    externalFilesModuleFactory.PsiModule.NotNull("externalFilesModuleFactory.PsiModule != null")
                        .TryGetFileByPath(prefabSourceFile.First(), out var sourceFile);
                    
                    if (sourceFile == null)
                        return;

                    var prefabFile = (IYamlFile)sourceFile.GetDominantPsiFile<YamlLanguage>();
                    
                    var prefabSourceObject = prefabFile.FindDocumentByAnchor(correspondingId.fileID); // It can be component, game object or prefab
                    // if it component we should query game object. 
                    var prefabStartGameObject = prefabSourceObject.GetUnityObjectDocumentFromFileIDProperty("m_GameObject") ?? prefabSourceObject;

                    var localModifications = GetPrefabModification(prefabInstance);
                    ProcessPrefabFromToRoot(yamlFileCache, externalFilesModuleFactory, prefabStartGameObject, localModifications, selector);
                    currentGameObject = GetTransformFromPrefab(prefabInstance);
                }
                else
                {
                    // TODO : check is currentGameObject transform or game object attached to this transform?
                    selector(currentGameObject.GetUnityObjectDocumentFromFileIDProperty("m_GameObject") ?? currentGameObject, modifications);
                    currentGameObject = currentGameObject.GetUnityObjectDocumentFromFileIDProperty("m_Father") 
                                        ?? FindTransformComponentForGameObject(currentGameObject).GetUnityObjectDocumentFromFileIDProperty("m_Father");
                }
            }
        }


        /// <summary>
        /// This method return path from component's owner to scene or prefab hierarachy root
        /// </summary>
        /// <param name="componentDocument">GameObject's component</param>
        /// <returns></returns>
        [NotNull]
        public static string GetGameObjectPathFromComponent([NotNull] IYamlDocument componentDocument)
        {
            var gameObjectDocument = componentDocument.GetUnityObjectDocumentFromFileIDProperty("m_GameObject") ?? componentDocument;

            var parts = new FrugalLocalList<string>();
            ProcessToRoot(gameObjectDocument, (document, modification) =>
            {
                string name = null;
                if (modification != null)
                {
                    var documentId = document.GetFileId();
                    name = GetValueFromModifications(modification, documentId, "m_Name");
                }
                else
                {
                    name = document.GetUnityObjectPropertyValue("m_Name").AsString();
                }

                if (name?.Equals(string.Empty) == true)
                    name = null;
                parts.Add(name ?? "INVALID");
            });

            if (parts.Count == 1)
                return parts[0];

            var sb = new StringBuilder();
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                sb.Append(parts[i]);
                sb.Append("\\");
            }

            return sb.ToString();
        }

        private static IBlockMappingNode GetPrefabModification(IYamlDocument yamlDocument)
        {
            // Prefab instance has a map of modifications, that stores delta of instance and prefab
            var prefabInstanceMap = (yamlDocument.BlockNode as IBlockMappingNode)
                ?.FindMapEntryBySimpleKey("PrefabInstance")?.Value as IBlockMappingNode;

            return prefabInstanceMap?.FindMapEntryBySimpleKey("m_Modification")?.Value as IBlockMappingNode;
        }

        private static IYamlDocument GetTransformFromPrefab(IYamlDocument prefabInstanceDocument)
        {
            // Prefab instance stores it's father in modification map
            var prefabModification = GetPrefabModification(prefabInstanceDocument);

            var fileID = prefabModification?.FindMapEntryBySimpleKey("m_TransformParent")?.Value.AsFileID();
            if (fileID == null)
                return null;

            var file = (IYamlFile) prefabInstanceDocument.GetContainingFile();
            return file.FindDocumentByAnchor(fileID.fileID);
        }

        [CanBeNull]
        public static ITypeElement GetTypeElementFromScriptAssetGuid(ISolution solution, [CanBeNull] string assetGuid)
        {
            if (assetGuid == null)
                return null;

            var cache = solution.GetComponent<MetaFileGuidCache>();
            var assetPaths = cache.GetAssetFilePathsFromGuid(assetGuid);
            if (assetPaths == null || assetPaths.IsEmpty())
                return null;

            // TODO: Multiple candidates!
            // I.e. someone has copy/pasted a .meta file
            if (assetPaths.Count != 1)
                return null;

            var projectItems = solution.FindProjectItemsByLocation(assetPaths[0]);
            var assetFile = projectItems.FirstOrDefault() as IProjectFile;
            if (!(assetFile?.GetPrimaryPsiFile() is ICSharpFile csharpFile))
                return null;

            var expectedClassName = assetPaths[0].NameWithoutExtension;
            var psiSourceFile = csharpFile.GetSourceFile();
            if (psiSourceFile == null)
                return null;

            var psiServices = csharpFile.GetPsiServices();
            var elements = psiServices.Symbols.GetTypesAndNamespacesInFile(psiSourceFile);
            foreach (var element in elements)
            {
                // Note that theoretically, there could be multiple classes with the same name in different namespaces.
                // Unity's own behaviour here is undefined - it arbitrarily chooses one
                // TODO: Multiple candidates in a file
                if (element is ITypeElement typeElement && typeElement.ShortName == expectedClassName)
                    return typeElement;
            }

            return null;
        }

        [CanBeNull]
        public static IYamlDocument FindTransformComponentForGameObject([CanBeNull] IYamlDocument gameObjectDocument)
        {
            // GameObject:
            //   m_Component:
            //   - component: {fileID: 1234567890}
            //   - component: {fileID: 1234567890}
            //   - component: {fileID: 1234567890}
            // One of these components is the RectTransform(GUI, 2D) or Transform(3D). Most likely the first, but we can't rely on order
            if (gameObjectDocument?.GetUnityObjectPropertyValue("m_Component") is IBlockSequenceNode components)
            {
                var file = (IYamlFile) gameObjectDocument.GetContainingFile();

                foreach (var componentEntry in components.EntriesEnumerable)
                {
                    // - component: {fileID: 1234567890}
                    var componentNode = componentEntry.Value as IBlockMappingNode;
                    var componentFileID = componentNode?.EntriesEnumerable.FirstOrDefault()?.Value.AsFileID();
                    if (componentFileID != null && !componentFileID.IsNullReference && !componentFileID.IsExternal)
                    {
                        var component = file.FindDocumentByAnchor(componentFileID.fileID);
                        var componentName = component.GetUnityObjectTypeFromRootNode();
                        if (componentName != null && (componentName.Equals("RectTransform") || componentName.Equals("Transform")))
                            return component;
                    }
                }
            }

            return null;
        }

        public static string GetValueFromModifications(IBlockMappingNode modification, string targetFileId, string value)
        {
            if (targetFileId != null && modification.FindMapEntryBySimpleKey("m_Modifications")?.Value is IBlockSequenceNode modifications)
            {
                foreach (var element in modifications.Entries)
                {
                    if (!(element.Value is IBlockMappingNode mod))
                        return null;
                    var type = (mod.FindMapEntryBySimpleKey("propertyPath")?.Value as IPlainScalarNode)
                        ?.Text.GetText();
                    var target = mod.FindMapEntryBySimpleKey("target")?.Value?.AsFileID();
                    if (type?.Equals(value) == true && target?.fileID.Equals(targetFileId) == true)
                    {
                        return (mod.FindMapEntryBySimpleKey("value")?.Value as IPlainScalarNode)?.Text.GetText();
                    }
                }
            }

            return null;
        }
    }
}