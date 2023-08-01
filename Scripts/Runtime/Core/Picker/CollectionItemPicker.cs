using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BrunoMikoski.ScriptableObjectCollections.Picker
{
    /// <summary>
    /// Collection Item Picker lets you pick one or more items from a collection, similar to how an enum field would
    /// work if the enum had the [Flags] attribute applied to it.
    /// </summary>
    [Serializable]
    public class CollectionItemPicker<TItemType> : IList<TItemType>
        where TItemType : ScriptableObject, ISOCItem
    {
        [SerializeField]
        private List<LongGuid> itemsGuids = new List<LongGuid>();


        private bool hasCachedCollection;
        private ScriptableObjectCollection cachedCollection;
        private ScriptableObjectCollection Collection
        {
            get
            {
                if (!hasCachedCollection)
                {
                    hasCachedCollection = CollectionsRegistry.Instance.TryGetCollectionFromItemType(typeof(TItemType),
                        out cachedCollection);
                }

                return cachedCollection;
            }
        }
        
        public event Action<TItemType> OnItemTypeAddedEvent;
        public event Action<TItemType> OnItemTypeRemovedEvent;
        public event Action OnChangedEvent;

        private bool isDirty = true;
        private List<TItemType> cachedItems = new();
        public List<TItemType> Items
        {
            get
            {
                if (isDirty)
                {
                    cachedItems.Clear();

                    foreach (ScriptableObject sObject in Collection)
                    {
                        if (sObject is TItemType itemType)
                        {
                            if (Contains(itemType))
                                cachedItems.Add(itemType);
                        }
                    }

                    isDirty = false;
                }

                return cachedItems;
            }
        }

        public CollectionItemPicker()
        {
            
        }
        
        public CollectionItemPicker(params TItemType[] items)
        {
            for (int i = 0; i < items.Length; i++)
                Add(items[i]);
        }

        #region Boleans and Checks
        public bool HasAny(params TItemType[] itemTypes)
        {
            for (int i = 0; i < itemTypes.Length; i++)
            {
                if (Contains(itemTypes[i]))
                    return true;
            }

            return false;
        }
        
        public bool HasAll(params TItemType[] itemTypes)
        {
            for (int i = 0; i < itemTypes.Length; i++)
            {
                if (!Contains(itemTypes[i]))
                    return false;
            }

            return true;
        }
        
        public bool HasNone(params TItemType[] itemTypes)
        {
            for (int i = 0; i < itemTypes.Length; i++)
            {
                if (Contains(itemTypes[i]))
                    return false;
            }

            return true;
        }
        #endregion
        
        //Implement mathematical operators  
        #region Operators

        public static CollectionItemPicker<TItemType> operator +(CollectionItemPicker<TItemType> picker1,
            CollectionItemPicker<TItemType> picker2)
        {
            CollectionItemPicker<TItemType> result = new CollectionItemPicker<TItemType>();

            for (int i = 0; i < picker1.Count; i++)
            {
                result.Add(picker1[i]);
            }

            for (int i = 0; i < picker2.Count; i++)
            {
                TItemType item = picker2[i];
                if (result.Contains(item))
                    continue;

                result.Add(item);
            }

            return result;
        }

        public static CollectionItemPicker<TItemType> operator -(CollectionItemPicker<TItemType> picker1,
            CollectionItemPicker<TItemType> picker2)
        {
            CollectionItemPicker<TItemType> result = new CollectionItemPicker<TItemType>();

            for (int i = 0; i < picker1.Count; i++)
            {
                result.Add(picker1[i]);
            }

            for (int i = 0; i < picker2.Count; i++)
            {
                TItemType item = picker2[i];
                if (!result.Contains(item))
                    continue;

                result.Remove(item);
            }

            return result;
        }

        public static CollectionItemPicker<TItemType> operator +(CollectionItemPicker<TItemType> picker,
            TItemType targetItem)
        {
            if (!picker.Contains(targetItem))
            {
                picker.Add(targetItem);
            }

            return picker;
        }

        public static CollectionItemPicker<TItemType> operator -(CollectionItemPicker<TItemType> picker,
            TItemType targetItem)
        {
            picker.Remove(targetItem);
            return picker;
        }

        #endregion

        // Implement IList and forward its members to items. This way we can conveniently use this thing as a list.
        #region IList members implementation

        public IEnumerator<TItemType> GetEnumerator()
        {
            return Items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Items.GetEnumerator();
        }

        public void Add(TItemType item)
        {
            if (Contains(item))
                return;
            
            itemsGuids.Add(item.GUID);
            isDirty = true;
            OnItemTypeAddedEvent?.Invoke(item);
            OnChangedEvent?.Invoke();
        }

        public void Clear()
        {
            itemsGuids.Clear();
            isDirty = true;
            OnChangedEvent?.Invoke();
        }

        public bool Contains(TItemType item)
        {
            for (int i = 0; i < itemsGuids.Count; i++)
            {
                if (itemsGuids[i] == item.GUID)
                    return true;
            }

            return false;
        }

        public void CopyTo(TItemType[] array, int arrayIndex)
        {
            for (int i = 0; i < itemsGuids.Count; i++)
            {
                if (!Collection.TryGetItemByGUID(itemsGuids[i], out ScriptableObject item))
                    continue;

                array[arrayIndex + i] = (TItemType) item;
            }
        }

        public bool Remove(TItemType item)
        {
            TItemType removedItem = item;
            bool removed = itemsGuids.Remove(item.GUID);
            if (removed)
            {
                isDirty = true;
                OnChangedEvent?.Invoke();
                OnItemTypeRemovedEvent?.Invoke(removedItem);
            }

            return removed;
        }

        public int Count => itemsGuids.Count;

        public bool IsReadOnly => false;

        public int IndexOf(TItemType item)
        {
            return itemsGuids.IndexOf(item.GUID);
        }

        public void Insert(int index, TItemType item)
        {
            itemsGuids.Insert(index, item.GUID);
            isDirty = true;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= itemsGuids.Count)
                return;

            if (!Collection.TryGetItemByGUID(itemsGuids[index], out var item))
                return;

            TItemType removedItem = (TItemType) item;
            itemsGuids.RemoveAt(index);
            isDirty = true;
            OnChangedEvent?.Invoke();
            OnItemTypeRemovedEvent?.Invoke(removedItem);
        }

        public TItemType this[int index]
        {
            get
            {
                if (!Collection.TryGetItemByGUID(itemsGuids[index], out var item))
                    return null;
                return item as TItemType;
            }
            set
            {
                itemsGuids[index] = value.GUID;
                isDirty = true;
            }
        }

        #endregion

    }
}
