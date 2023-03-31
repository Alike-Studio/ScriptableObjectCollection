using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEditor.IMGUI.Controls;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BrunoMikoski.ScriptableObjectCollections
{
    [CustomEditor(typeof(ScriptableObjectCollection), true)]
    public sealed class CollectionCustomEditor : Editor
    {
        private const string WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY = "WaitingForScriptTobeCreated";
        private static ScriptableObject LAST_ADDED_COLLECTION_ITEM;

        private ScriptableObjectCollection collection;
        private string searchString = "";

        private SearchField searchField;
        private bool showSettings;

        private float[] heights;
        private bool[] itemHidden;
        private ReorderableList reorderableList;
        private SerializedProperty itemsSerializedProperty;
        private int lastCheckedForValidItemsArraySize;

        private static bool IsWaitingForNewTypeBeCreated
        {
            get => EditorPrefs.GetBool(WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY, false);
            set => EditorPrefs.SetBool(WAITING_FOR_SCRIPT_TO_BE_CREATED_KEY, value);
        }
        private CollectionsSharedSettings InstanceCollectionSettings => CollectionsRegistry.Instance.CollectionSettings;

        public void OnEnable()
        {
            collection = (ScriptableObjectCollection)target;

            if (!CollectionsRegistry.Instance.IsKnowCollection(collection))
                CollectionsRegistry.Instance.ReloadCollections();
            
            itemsSerializedProperty = serializedObject.FindProperty("items");

            ValidateCollectionItems();

            CreateReorderableList();

            CheckGeneratedCodeLocation();
            CheckIfCanBePartial();
            CheckGeneratedStaticFileName();
            ValidateGeneratedFileNamespace();
        }

        private void CreateReorderableList()
        {
            reorderableList = new ReorderableList(serializedObject, itemsSerializedProperty, true, true, true, true);
            reorderableList.drawElementCallback += DrawCollectionItemAtIndex;
            reorderableList.elementHeightCallback += GetCollectionItemHeight;
            reorderableList.onAddDropdownCallback += OnClickToAddNewItem;
            reorderableList.onRemoveCallback += OnClickToRemoveItem;
            reorderableList.onReorderCallback += OnListOrderChanged;
            reorderableList.drawHeaderCallback += OnDrawerHeader;
        }

        private void OnDisable()
        {
            if (reorderableList == null)
                return;

            reorderableList.drawElementCallback -= DrawCollectionItemAtIndex;
            reorderableList.elementHeightCallback -= GetCollectionItemHeight;
            reorderableList.onAddDropdownCallback -= OnClickToAddNewItem;
            reorderableList.onRemoveCallback -= OnClickToRemoveItem;
            reorderableList.onReorderCallback -= OnListOrderChanged;
            reorderableList.drawHeaderCallback -= OnDrawerHeader;
        }

        private void OnDrawerHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, "Items", EditorStyles.boldLabel);
        }

        private void OnListOrderChanged(ReorderableList list)
        {
            list.serializedProperty.serializedObject.ApplyModifiedProperties();
        }

        private void OnClickToRemoveItem(ReorderableList list)
        {
            int selectedIndex = list.index;
            RemoveItemAtIndex(selectedIndex);
        }

        private void RemoveItemAtIndex(int selectedIndex)
        {
            SerializedProperty selectedProperty = reorderableList.serializedProperty.GetArrayElementAtIndex(selectedIndex);
            Object asset = selectedProperty.objectReferenceValue;
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset));
            AssetDatabase.SaveAssets();
            reorderableList.serializedProperty.DeleteArrayElementAtIndex(selectedIndex);
            reorderableList.serializedProperty.serializedObject.ApplyModifiedProperties();
        }

        private void OnClickToAddNewItem(Rect buttonRect, ReorderableList list)
        {
            AddNewItem();
        }

        private float GetCollectionItemHeight(int index)
        {
            if (itemHidden == null || itemHidden.Length == 0 || itemHidden[index] || index > itemHidden.Length - 1)
                return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            return Mathf.Max(
                heights[index],
                EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing
            );
        }

        private void DrawCollectionItemAtIndex(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty collectionItemSerializedProperty = reorderableList.serializedProperty.GetArrayElementAtIndex(index);

            if (itemHidden[index] || collectionItemSerializedProperty.objectReferenceValue == null)
                return;
            
            float originY = rect.y;

            rect.height = EditorGUIUtility.singleLineHeight;
            rect.x += 10;
            rect.width -= 20;

            Rect foldoutArrowRect = rect;
            bool wasExpanded = collectionItemSerializedProperty.isExpanded;
            collectionItemSerializedProperty.isExpanded = EditorGUI.Foldout(
                foldoutArrowRect,
                collectionItemSerializedProperty.isExpanded,
                GUIContent.none
            );

            if (!wasExpanded && collectionItemSerializedProperty.isExpanded)
            {
                if (Event.current.alt)
                    SetAllExpanded(true);
            }
            else if (wasExpanded && !collectionItemSerializedProperty.isExpanded)
            {
                if (Event.current.alt)
                    SetAllExpanded(false);
            }

            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                GUI.SetNextControlName(collectionItemSerializedProperty.objectReferenceValue.name);
                Rect nameRect = rect;
                string newName = EditorGUI.DelayedTextField(nameRect, collectionItemSerializedProperty.objectReferenceValue.name, CollectionEditorGUI.ItemNameStyle);
                
                if (LAST_ADDED_COLLECTION_ITEM == collectionItemSerializedProperty.objectReferenceValue)
                {
                    EditorGUI.FocusTextInControl( collectionItemSerializedProperty.objectReferenceValue.name);
                    reorderableList.index = index;
                    LAST_ADDED_COLLECTION_ITEM = null;
                }
                
                if (changeCheck.changed)
                {
                    if (newName.IsReservedKeyword())
                    {
                        Debug.LogError($"{newName} is a reserved C# keyword, will cause issues with " +
                                       $"code generation, reverting to previous name");
                    }
                    else
                    {
                        AssetDatabaseUtils.RenameAsset(collectionItemSerializedProperty.objectReferenceValue, newName);
                        AssetDatabase.SaveAssets();
                    }
                }
            }

            rect.y += EditorGUIUtility.singleLineHeight;

            if (collectionItemSerializedProperty.isExpanded)
            {
                rect.y += EditorGUIUtility.standardVerticalSpacing; 

                SerializedObject collectionItemSerializedObject = new SerializedObject(collectionItemSerializedProperty.objectReferenceValue);
                
                EditorGUI.indentLevel++;
                
                SerializedProperty iterator = collectionItemSerializedObject.GetIterator();

                using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
                {
                    for (bool enterChildren = true; iterator.NextVisible(enterChildren); enterChildren = false)
                    {
                        bool guiEnabled = GUI.enabled;
                        if (iterator.displayName.Equals("Script"))
                            GUI.enabled = false;

                        EditorGUI.PropertyField(rect, iterator, true);
                        GUI.enabled = guiEnabled;

                        rect.y += EditorGUI.GetPropertyHeight(iterator, true) +
                                  EditorGUIUtility.standardVerticalSpacing;
                    }

                    if (changeCheck.changed)
                        iterator.serializedObject.ApplyModifiedProperties();
                }

                EditorGUI.indentLevel--;
            }

            CheckForContextInputOnItem(collectionItemSerializedProperty, index, originY, rect);
            
            heights[index] = rect.y - originY;
        }

        private void SetAllExpanded(bool expanded)
        {
            for (int i = 0; i < reorderableList.count; i++)
            {
                SerializedProperty property = reorderableList.serializedProperty.GetArrayElementAtIndex(i);
                property.isExpanded = expanded;
            }
        }

        private void CheckForContextInputOnItem(SerializedProperty collectionItemSerializedProperty, int index, float originY, Rect rect)
        {
            Event current = Event.current;

            Rect contextRect = rect;
            contextRect.height = rect.y - originY;
            contextRect.y = originY;
            contextRect.x -= 30;
            contextRect.width += 50;
            
            if(contextRect.Contains(current.mousePosition) &&  current.type == EventType.ContextClick)
            {
                ScriptableObject scriptableObject = collectionItemSerializedProperty.objectReferenceValue as ScriptableObject;

                GenericMenu menu = new GenericMenu();

                menu.AddItem(
                    new GUIContent("Copy Values"),
                    false,
                    () =>
                    {
                        CopyCollectionItemUtils.SetSource(scriptableObject);
                    }
                );
                if (CopyCollectionItemUtils.CanPasteToTarget(scriptableObject))
                {
                    menu.AddItem(
                        new GUIContent("Paste Values"),
                        false,
                        () =>
                        {
                            CopyCollectionItemUtils.ApplySourceToTarget(scriptableObject);
                        }
                    );
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Paste Values"));
                }
                menu.AddSeparator("");

                menu.AddItem(
                    new GUIContent("Duplicate Item"),
                    false,
                    () =>
                    {
                        DuplicateItem(index);
                    }
                );
                
                menu.AddItem(
                    new GUIContent("Delete Item"),
                    false,
                    () =>
                    {
                        RemoveItemAtIndex(index);
                    }
                );
                
                menu.AddSeparator("");
                menu.AddItem(
                    new GUIContent("Select Asset"),
                    false,
                    () =>
                    {
                        SelectItemAtIndex(index);
                    }
                );
                
                menu.ShowAsContext();
 
                current.Use(); 
            }
        }

        private void SelectItemAtIndex(int index)
        {
            SerializedProperty serializedProperty = itemsSerializedProperty.GetArrayElementAtIndex(index);
            ScriptableObject collectionItem = serializedProperty.objectReferenceValue as ScriptableObject;
            Selection.objects = new Object[] { collectionItem };
        }

        private void DuplicateItem(int index)
        {
            SerializedProperty serializedProperty = itemsSerializedProperty.GetArrayElementAtIndex(index);
            ScriptableObject collectionItem = serializedProperty.objectReferenceValue as ScriptableObject;
            string collectionItemAssetPath = AssetDatabase.GetAssetPath(collectionItem);
            string path = Path.GetDirectoryName(collectionItemAssetPath);
            string cloneName = collectionItem.name + " Clone";
            if (AssetDatabase.CopyAsset(collectionItemAssetPath, $"{path}/{cloneName}.asset"))
            {
                AssetDatabase.SaveAssets();
                ScriptableObject clonedItem = AssetDatabase.LoadAssetAtPath<ScriptableObject>($"{path}/{cloneName}.asset");
                ISOCItem socItem = clonedItem as ISOCItem;
                if (socItem == null)
                    throw new Exception($"Cloned item {clonedItem.name} is not an ISOCItem");
                
                socItem.GenerateGUID();
                itemsSerializedProperty.InsertArrayElementAtIndex(index + 1);
                SerializedProperty clonedItemSerializedProperty = itemsSerializedProperty.GetArrayElementAtIndex(index + 1);
                clonedItemSerializedProperty.objectReferenceValue = clonedItem;
                clonedItemSerializedProperty.serializedObject.ApplyModifiedProperties();
                clonedItemSerializedProperty.isExpanded = true;
                LAST_ADDED_COLLECTION_ITEM = clonedItem;
            }
        }

        public override void OnInspectorGUI()
        {
            ValidateCollectionItems();
            CheckHeightsAndHiddenArraySizes();

            using (new GUILayout.VerticalScope("Box"))
            {
                DrawSearchField();
                DrawSynchronizeButton();
                reorderableList.DoLayoutList();
                DrawBottomMenu();
            }
            DrawSettings();
            CheckForKeyboardShortcuts();
        }

        private void CheckHeightsAndHiddenArraySizes()
        {
            if (heights == null || heights.Length != itemsSerializedProperty.arraySize)
                heights = new float[itemsSerializedProperty.arraySize];

            if (itemHidden == null || itemHidden.Length != itemsSerializedProperty.arraySize)
                itemHidden = new bool[itemsSerializedProperty.arraySize];
        }

        private void DrawSynchronizeButton()
        {
            if (GUILayout.Button("Synchronize Assets"))
            {
                serializedObject.Update();
                CheckHeightsAndHiddenArraySizes();
            }
        }

        private void CheckForKeyboardShortcuts()
        {
            if (reorderableList.index == -1)
                return;

            if (!reorderableList.HasKeyboardControl())
                return;
            
            if (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint)
                return;

            if (reorderableList.index > reorderableList.serializedProperty.arraySize - 1)
                return;
            
            SerializedProperty element = reorderableList.serializedProperty.GetArrayElementAtIndex(reorderableList.index);

            if (Event.current.keyCode == KeyCode.RightArrow)
            {
                element.isExpanded = true; 
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.LeftArrow)
            {
                element.isExpanded = false; 
                Event.current.Use();
            }
        }

        private void ValidateCollectionItems()
        {
            if (lastCheckedForValidItemsArraySize == itemsSerializedProperty.arraySize)
                return;
            
            bool modified = false;
            for (int i = itemsSerializedProperty.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty itemProperty = itemsSerializedProperty.GetArrayElementAtIndex(i);
                if (itemProperty.objectReferenceValue == null)
                {
                    itemsSerializedProperty.DeleteArrayElementAtIndex(i);
                    modified = true;
                    Debug.LogWarning($"Removing SOCItem at index {i} because it is null");
                    continue;
                }

                ISOCItem socItem = (ScriptableObject) itemProperty.objectReferenceValue as ISOCItem;
                if (socItem == null)
                {
                    itemsSerializedProperty.DeleteArrayElementAtIndex(i);
                    modified = true;
                    Debug.LogWarning($"Removing SOCItem at index {i} because it is not a ISOCItem");
                    continue;
                }

                if (socItem.Collection == null)
                    socItem.SetCollection(collection);
            }

            if (modified)
                itemsSerializedProperty.serializedObject.ApplyModifiedProperties();
            
            lastCheckedForValidItemsArraySize = itemsSerializedProperty.arraySize;
        }

        private void DrawBottomMenu()
        {
            using (new EditorGUILayout.HorizontalScope("Box"))
            {
                if (GUILayout.Button($"Generate Static Access File", EditorStyles.miniButtonRight))
                {
                    EditorApplication.delayCall += () =>
                    {
                        CodeGenerationUtility.GenerateStaticCollectionScript(collection);
                    };
                }
            }
        }
        
        private void AddNewItem()
        {
            List<Type> itemsSubclasses = new List<Type> {collection.GetItemType()};

            TypeCache.TypeCollection sub = TypeCache.GetTypesDerivedFrom(collection.GetItemType());
            for (int i = 0; i < sub.Count; i++)
            {
                itemsSubclasses.Add(sub[i]);
            }

            GenericMenu optionsMenu = new GenericMenu();

            for (int i = 0; i < itemsSubclasses.Count; i++)
            {
                Type itemSubClass = itemsSubclasses[i];
                if (itemSubClass.IsAbstract)
                    continue;
                
                AddMenuOption(optionsMenu, itemSubClass.Name, () =>
                {
                    EditorApplication.delayCall += () => { AddNewItemOfType(itemSubClass); };
                });
            }
                
            optionsMenu.AddSeparator("");
            
            for (int i = 0; i < itemsSubclasses.Count; i++)
            {
                Type itemSubClass = itemsSubclasses[i];

                if (itemSubClass.IsSealed)
                    continue;
                
                AddMenuOption(optionsMenu, $"Create New/class $NEW : {itemSubClass.Name}", () =>
                {
                    EditorApplication.delayCall += () => { CreateAndAddNewItemOfType(itemSubClass); };
                });
            }

            optionsMenu.ShowAsContext();
        }

        private void CreateAndAddNewItemOfType(Type itemSubClass)
        {
            CreateNewCollectionItemFromBaseWizard.Show(itemSubClass, success =>
            {
                if (success)
                {
                    IsWaitingForNewTypeBeCreated = true;
                }
            });
        }

        [DidReloadScripts]
        public static void AfterStaticAssemblyReload()
        {
            if (!IsWaitingForNewTypeBeCreated)
                return;

            IsWaitingForNewTypeBeCreated = false;

            string lastGeneratedCollectionScriptPath =
                CreateNewCollectionItemFromBaseWizard.LastGeneratedCollectionScriptPath.Value;
            string lastCollectionFullName = CreateNewCollectionItemFromBaseWizard.LastCollectionFullName.Value;

            if (string.IsNullOrEmpty(lastGeneratedCollectionScriptPath))
                return;
            
            CreateNewCollectionItemFromBaseWizard.LastCollectionFullName.Value = string.Empty;
            CreateNewCollectionItemFromBaseWizard.LastGeneratedCollectionScriptPath.Value = string.Empty;

            string assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(lastGeneratedCollectionScriptPath);

            Type targetType = Type.GetType($"{lastCollectionFullName}, {assemblyName}");

            if (CollectionsRegistry.Instance.TryGetCollectionFromItemType(targetType,
                out ScriptableObjectCollection collection))
            {
                Selection.activeObject = null;
                LAST_ADDED_COLLECTION_ITEM =  collection.AddNew(targetType);
                
                EditorApplication.delayCall += () =>
                {
                    Selection.activeObject = collection;
                };
            }
        }
 
        private void AddNewItemOfType(Type targetType)
        {
            LAST_ADDED_COLLECTION_ITEM = collection.AddNew(targetType);
            itemsSerializedProperty.arraySize++;
            SerializedProperty arrayElementAtIndex = itemsSerializedProperty.GetArrayElementAtIndex(itemsSerializedProperty.arraySize - 1);
            arrayElementAtIndex.objectReferenceValue = LAST_ADDED_COLLECTION_ITEM;
            arrayElementAtIndex.isExpanded = true;
        }

        private void AddMenuOption(GenericMenu optionsMenu, string displayName, Action action)
        {
            optionsMenu.AddItem(new GUIContent(displayName), false, action.Invoke);
        }

        private void DrawSearchField()
        {                
            Rect searchRect =
                GUILayoutUtility.GetRect(1, 1, 20, 20, GUILayout.ExpandWidth(true));

            if (searchField == null)
                searchField = new SearchField();

            using (EditorGUI.ChangeCheckScope changeCheckScope = new EditorGUI.ChangeCheckScope())
            {
                searchString = searchField.OnGUI(searchRect, searchString);

                if (changeCheckScope.changed)
                {
                    if (string.IsNullOrEmpty(searchString))
                    {
                        for (int i = 0; i < itemHidden.Length; i++)
                            itemHidden[i] = false;
                    }
                    else
                    {
                        for (int i = 0; i < itemsSerializedProperty.arraySize; i++)
                        {
                            SerializedProperty arrayElementAtIndex = itemsSerializedProperty.GetArrayElementAtIndex(i);
                            if (arrayElementAtIndex.objectReferenceValue.name.IndexOf(searchString, StringComparison.CurrentCultureIgnoreCase) == -1)
                                itemHidden[i] = true;
                        }
                    }
                }
            }

            EditorGUILayout.Separator();
        }

        private void DrawSettings()
        {
            using (new GUILayout.VerticalScope("Box"))
            {
                EditorGUI.indentLevel++;
                showSettings = EditorGUILayout.Foldout(showSettings, "Advanced", true);
                EditorGUI.indentLevel--;

                if (showSettings)
                {
                    EditorGUI.indentLevel++;

                    DrawAutomaticallyLoaded();
                    DrawGeneratedClassParentFolder();
                    DrawPartialClassToggle();
                    DrawUseBaseClassToggle();
                    DrawGeneratedFileName();
                    DrawGeneratedFileNamespace();
                    GUILayout.Space(10);
                    DrawDeleteCollection();
                    
                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawDeleteCollection()
        {
            Color backgroundColor = GUI.backgroundColor;
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Delete Collection"))
            {
                DeleteCollectionEditorWindow.DeleteCollection(collection);
            }

            GUI.backgroundColor = backgroundColor;
        }

        private void DrawGeneratedFileName()
        {
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                string newFileName = EditorGUILayout.DelayedTextField("Static File Name", InstanceCollectionSettings.GetCollectionGeneratedStaticClassFileName(collection));
                if (changeCheck.changed)
                {
                    InstanceCollectionSettings.SetCollectionGeneratedStaticClassFileName(collection,
                        newFileName);
                }
            }
        }

        private void DrawGeneratedFileNamespace()
        {
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                string newNameSpace = EditorGUILayout.DelayedTextField("Namespace", InstanceCollectionSettings.GetCollectionGeneratedStaticFileNamespace(collection));
                if (changeCheck.changed)
                {
                    InstanceCollectionSettings.SetCollectionGeneratedStaticFileNamespace(collection,
                        newNameSpace);
                }
            }
        }

        private void ValidateGeneratedFileNamespace()
        {
            if (string.IsNullOrEmpty(InstanceCollectionSettings.GetCollectionGeneratedStaticFileNamespace(collection)))
            {
                if (collection != null)
                {
                    string targetNamespace = collection.GetItemType().Namespace;
                    if (!string.IsNullOrEmpty(targetNamespace))
                    {
                        InstanceCollectionSettings.SetCollectionGeneratedStaticFileNamespace(collection,
                            targetNamespace);
                    }
                }
            }
        }
        
        private void DrawAutomaticallyLoaded()
        {
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                
                bool isAutomaticallyLoaded = EditorGUILayout.Toggle("Automatically Loaded", InstanceCollectionSettings.IsCollectionAutoLoaded(collection));
                if (changeCheck.changed)
                    InstanceCollectionSettings.SetCollectionAutoLoaded(collection, isAutomaticallyLoaded);
            }
        }

        private void DrawGeneratedClassParentFolder()
        {
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                DefaultAsset pathObject = AssetDatabase.LoadAssetAtPath<DefaultAsset>(InstanceCollectionSettings.GetCollectionGeneratedFileLocationPath(collection));
                if (pathObject == null && !string.IsNullOrEmpty(CollectionsRegistry.Instance.CollectionSettings.GeneratedScriptsDefaultFilePath))
                {
                    pathObject = AssetDatabase.LoadAssetAtPath<DefaultAsset>(CollectionsRegistry.Instance.CollectionSettings.GeneratedScriptsDefaultFilePath);
                }
                
                pathObject = (DefaultAsset) EditorGUILayout.ObjectField(
                    "Generated Scripts Parent Folder",
                    pathObject,
                    typeof(DefaultAsset),
                    false
                );
                string assetPath = AssetDatabase.GetAssetPath(pathObject);

                if (changeCheck.changed || !string.Equals(InstanceCollectionSettings.GetCollectionGeneratedFileLocationPath(collection), assetPath, StringComparison.Ordinal))
                {
                    InstanceCollectionSettings.SetCollectionGeneratedFileLocationPath(collection, assetPath);

                    if (string.IsNullOrEmpty(CollectionsRegistry.Instance.CollectionSettings
                            .GeneratedScriptsDefaultFilePath))
                    {
                        CollectionsRegistry.Instance.CollectionSettings.SetGeneratedScriptsDefaultFilePath(assetPath);
                    }
                }
            }
        }

        private void DrawPartialClassToggle()
        {
            bool canBePartial= CheckIfCanBePartial();
            
            EditorGUI.BeginDisabledGroup(!canBePartial);
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                bool writeAsPartial = EditorGUILayout.Toggle("Write as Partial Class",
                    InstanceCollectionSettings.IsCollectionGenerateAsPartialClass(collection));
                
                if (changeCheck.changed)
                {
                    InstanceCollectionSettings.SetGenerateAsPartialClass(collection, writeAsPartial);
                }
            }

            EditorGUI.EndDisabledGroup();
        }
        
        private void DrawUseBaseClassToggle()
        {
            using (EditorGUI.ChangeCheckScope changeCheck = new EditorGUI.ChangeCheckScope())
            {
                bool useBaseClass = EditorGUILayout.Toggle("Use Base Class for items", InstanceCollectionSettings.IsCollectionGenerateAsBaseClass(collection));
                if (changeCheck.changed)
                {
                    InstanceCollectionSettings.SetCollectionGenerateAsBaseClass(collection, useBaseClass);
                }
            }
        }
        
        private void CheckGeneratedStaticFileName()
        {
            if (!string.IsNullOrEmpty(InstanceCollectionSettings.GetCollectionGeneratedStaticClassFileName(collection)))
                return;

            if (collection.name.Equals(collection.GetItemType().Name, StringComparison.Ordinal) 
                && InstanceCollectionSettings.IsCollectionGenerateAsPartialClass(collection))
            {
                InstanceCollectionSettings.SetCollectionGeneratedStaticClassFileName(collection,
                    $"{collection.GetItemType().Name}Static");
            }
            else
            {
                InstanceCollectionSettings.SetCollectionGeneratedStaticClassFileName(collection,
                    $"{collection.name}Static".Sanitize().FirstToUpper());
            }
        }

        private bool CheckIfCanBePartial()
        {
            string baseClassPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(collection));
            string baseAssembly = CompilationPipeline.GetAssemblyNameFromScriptPath(baseClassPath);
            string targetGeneratedCodePath = CompilationPipeline.GetAssemblyNameFromScriptPath(InstanceCollectionSettings.GetCollectionGeneratedFileLocationPath(collection));
            
            // NOTE: If you're not using assemblies for your code, it's expected that 'targetGeneratedCodePath' would
            // be the same as 'baseAssembly', but it isn't. 'targetGeneratedCodePath' seems to be empty in that case.
            bool canBePartial = baseAssembly.Equals(targetGeneratedCodePath, StringComparison.Ordinal) ||
                                string.IsNullOrEmpty(targetGeneratedCodePath);
            
            if (InstanceCollectionSettings.IsCollectionGenerateAsPartialClass(collection) && !canBePartial)
            {
                InstanceCollectionSettings.SetGenerateAsPartialClass(collection, false);
            }

            return canBePartial;
        }

        private void CheckGeneratedCodeLocation()
        {
            if (!string.IsNullOrEmpty(InstanceCollectionSettings.GetCollectionGeneratedFileLocationPath(collection)))
                return;

            
            if (!string.IsNullOrEmpty(InstanceCollectionSettings.GeneratedScriptsDefaultFilePath))
            {
                InstanceCollectionSettings.SetCollectionGeneratedFileLocationPath(collection,
                    InstanceCollectionSettings.GeneratedScriptsDefaultFilePath);
            }
            else
            {
                string collectionScriptPath =
                    Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(collection)));

                InstanceCollectionSettings.SetCollectionGeneratedFileLocationPath(collection,
                    collectionScriptPath);
            }
        }

        public static ScriptableObject AddNewItem(ScriptableObjectCollection collection, Type itemType)
        {
            ScriptableObject collectionItem = collection.AddNew(itemType);
            Selection.objects = new Object[] {collection};
            LAST_ADDED_COLLECTION_ITEM = collectionItem;
            return collectionItem;
        }
    }
}
