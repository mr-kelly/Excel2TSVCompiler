﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CosmosConfigurator
{
    public class AutoParseAttribute : Attribute
    {
        
    }
    public class TabColumnAttribute : Attribute
    {

    }

    /// <summary>
    /// 表头信息
    /// </summary>
    public class HeaderInfo
    {
        public int ColumnIndex;
        public string HeaderName;
        public string HeaderDef;
    }

    public class TabFileConfig
    {
        public string Content;

        public char[] Separators = new char[] { '\t' };
        public Action<string> OnExceptionEvent;
    }

    public class TabFile : TabFile<DefaultTabRow>
    {
        public TabFile(string content)
            : base(content)
        {
        }

        public TabFile(TabFileConfig config)
            : base(config)
        {
        }
    }

    public partial class TabFile<T> : IDisposable where T : TabRow, new()  // IEnumerable<TabRow3<T>>, 
    {
        private readonly TabFileConfig _config;

        public TabFile(string content)
            : this(new TabFileConfig()
                {
                    Content = content
                })
        {
        }

        public TabFile()
            : this(new TabFileConfig())
        {
        }

        public TabFile(TabFileConfig config)
        {
            _config = config;

            if (!string.IsNullOrEmpty(_config.Content))
                ParseString(_config.Content);
        }


        protected internal int _colCount;  // 列数

        protected internal Dictionary<string, HeaderInfo> Headers = new Dictionary<string, HeaderInfo>();
        protected internal Dictionary<int, string[]> TabInfo = new Dictionary<int, string[]>();

        /// <summary>
        /// Row Id to Rows , start from 1
        /// </summary>
        protected internal Dictionary<int, T> Rows = new Dictionary<int, T>();

        /// <summary>
        /// Store the Primary Key to Rows
        /// </summary>
        protected Dictionary<object, T> PrimaryKey2Row = new Dictionary<object, T>();

        public Dictionary<string, HeaderInfo>.KeyCollection HeaderNames
        {
            get { return Headers.Keys; }
        }

        // 直接从字符串分析
        public static TabFile<T> LoadFromString(string content)
        {
            TabFile<T> tabFile = new TabFile<T>(content);
            tabFile.ParseString(content);

            return tabFile;
        }

        // 直接从文件, 传入完整目录，跟通过资源管理器自动生成完整目录不一样，给art库用的
        public static TabFile<T> LoadFromFile(string fileFullPath)
        {
            using (FileStream fileStream = new FileStream(fileFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            // 不会锁死, 允许其它程序打开
            {

                StreamReader oReader = new StreamReader(fileStream, System.Text.Encoding.UTF8);
                return new TabFile<T>(oReader.ReadToEnd());
            }
        }

        protected bool ParseReader(TextReader oReader)
        {
            // 首行
            var headLine = oReader.ReadLine();
            if (headLine == null)
            {
                OnExeption("Head Line null");
                return false;
            }

            var defLine = oReader.ReadLine(); // 声明行
            if (defLine == null)
            {
                OnExeption("Statemen Line (Line2) Null");
                return false;
            }

            var defLineArr = defLine.Split(_config.Separators, StringSplitOptions.None);

            string[] firstLineSplitString = headLine.Split(_config.Separators, StringSplitOptions.None);  // don't remove RemoveEmptyEntries!
            string[] firstLineDef = new string[firstLineSplitString.Length];
            Array.Copy(defLineArr, 0, firstLineDef, 0, defLineArr.Length);  // 拷贝，确保不会超出表头的

            for (int i = 1; i <= firstLineSplitString.Length; i++)
            {
                var headerString = firstLineSplitString[i - 1];

                var headerInfo = new HeaderInfo
                {
                    ColumnIndex = i,
                    HeaderName = headerString,
                    HeaderDef = firstLineDef[i - 1],
                };

                Headers[headerInfo.HeaderName] = headerInfo;
            }
            _colCount = firstLineSplitString.Length;  // 標題

            // 读取行内容

            T cachedNewObj = null;

            string sLine = "";
            int rowIndex = 1; // 从第1行开始
            while (sLine != null)
            {
                sLine = oReader.ReadLine();
                if (sLine != null)
                {
                    string[] splitString1 = sLine.Split(_config.Separators, StringSplitOptions.None);

                    TabInfo[rowIndex] = splitString1;

                    var newT = cachedNewObj ?? (cachedNewObj = new T());  // the New Object may not be used this time, so cache it!
                    newT.RowNumber = rowIndex;
                    
                    if (!newT.IsAutoParse)
                        newT.Parse(splitString1);
                    else
                        AutoParse(newT, splitString1);

                    if (newT.PrimaryKey != null)
                    {
                        T oldT;
                        if (!PrimaryKey2Row.TryGetValue(newT.PrimaryKey, out oldT))  // 原本不存在，使用new的，释放cacheNew，下次直接new
                        {
                            PrimaryKey2Row[newT.PrimaryKey] = newT;
                            cachedNewObj = null; // release the Cache!
                        }
                        else  // 原本存在，使用old的， cachedNewObj(newT)因此残留, 留待下回合使用
                        {
                            // Check Duplicated Primary Key, 使用原来的，不使用新new出来的, 下回合直接用_cachedNewObj
                            OnExeption("[Duplicated Primary Key]: {0}", oldT.PrimaryKey);
                            newT = oldT;
                        }
                    }

                    Rows[rowIndex] = newT;
                    rowIndex++;
                }
            }

            return true;
        }

        internal FieldInfo[] TabFields
        {
            get
            {
                return (from p in typeof(T).GetFields()
                        from attribute in p.GetCustomAttributes(typeof(TabColumnAttribute), true)
                        select p).ToArray();
            }
        }

        internal PropertyInfo[] TabProperties
        {
            get
            {
				return (from p in typeof(T).GetProperties() from attribute in p.GetCustomAttributes(typeof(TabColumnAttribute), true) select p).ToArray();
            }
        }

        protected void AutoParse(TabRow tabRow, string[] cellStrs)
        {
            var type = tabRow.GetType();
            var okFields = new List<FieldInfo>();

            foreach (FieldInfo field in TabFields)
            {
                if (!HasColumn(field.Name))
                {
                    OnExeption("表{0} 找不到表头{1}", type.Name, field.Name);
                    continue;
                }
                okFields.Add(field);
            }

            foreach (var field in okFields)
            {
                var fieldName = field.Name;
                var fieldType = field.FieldType;
                var methodName = string.Format("Get_{0}", fieldType.Name);
                var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (method != null)
                {
                    var index = (from kv in Headers where kv.Value.HeaderName == fieldName select kv.Value.ColumnIndex).First();
                    field.SetValue(tabRow, method.Invoke(tabRow, new object[]
                    {
                       cellStrs[index] , null
                    }));
                }
                else
                {
                    OnExeption("Not Find the Method {0}", methodName);
                }
            }

        }
        protected bool ParseString(string content)
        {
            using (var oReader = new StringReader(content))
            {
                ParseReader(oReader);
            }

            return true;
        }

        public bool HasColumn(string colName)
        {
            return Headers.ContainsKey(colName);
        }

        protected internal void OnExeption(string message, params object[] args)
        {
            if (_config.OnExceptionEvent == null)
                throw new Exception(string.Format(message, args));
            else
            {
                _config.OnExceptionEvent(string.Format(message, args));
            }
        }

        public int GetHeight()
        {
            return Rows.Count;
        }

        public int GetColumnCount()
        {
            return _colCount;
        }

        public int GetWidth()
        {
            return _colCount;
        }

        public T GetRow(int row)
        {
            T rowT;
            if (!Rows.TryGetValue(row, out rowT))
            {
                rowT = Rows[row] = new T();
            }

            return rowT;
        }

        //public bool SetValue<T>(int row, int column, T value) where T : TabRow, 
        //{
        //    if (row > Rows.Count || column > _colCount || row <= 0 || column <= 0)  //  || column > ColIndex.Count
        //    {
        //        throw new Exception(string.Format("Wrong row-{0} or column-{1}", row, column));
        //        return false;
        //    }
        //    string content = Convert.ToString(value);
        //    if (row == 0)
        //    {
        //        foreach (var kv in Headers)
        //        {
        //            if (kv.Value.ColumnIndex == column)
        //            {
        //                Headers.Remove(kv.Key);
        //                Headers[content] = kv.Value;
        //                break;
        //            }
        //        }
        //    }
        //    T rowT;
        //    if (!Rows.TryGetValue(row, out rowT))
        //    {
        //        //rowT = Rows[row] = new T();
        //    }
        //    var rowStrs = TabInfo[row];
        //    if (column - 1 >= rowStrs.Length) // 超出, 扩充
        //    {
        //        var oldRowStrs = rowStrs;
        //        rowStrs = TabInfo[row] = new string[column];
        //        oldRowStrs.CopyTo(rowStrs, 0);
        //    }
        //    rowStrs[column - 1] = content;
        //    return true;
        //}

        //public bool SetValue<T>(int row, string columnName, T value)
        //{
        //    HeaderInfo headerInfo;
        //    if (!Headers.TryGetValue(columnName, out headerInfo))
        //        return false;

        //    return SetValue(row, headerInfo.ColumnIndex, value);
        //}

        //IEnumerator<TabRow3<T>> IEnumerable<TabRow3<T>>.GetEnumerator()
        //{
        //    int rowStart = 1;
        //    for (int i = rowStart; i <= GetHeight(); i++)
        //    {
        //        _rowInteratorCache.Row = i;
        //        yield return _rowInteratorCache;
        //    }
        //}

        //public IEnumerator GetEnumerator()
        //{
        //    int rowStart = 1;
        //    for (int i = rowStart; i <= GetHeight(); i++)
        //    {
        //        _rowInteratorCache.Row = i;
        //        yield return _rowInteratorCache;
        //    }
        //}

        public void Dispose()
        {
            this.Headers.Clear();
            this.TabInfo.Clear();
            this.Rows.Clear();
            this.PrimaryKey2Row.Clear();
        }

        public void Close()
        {
            Dispose();
        }

        public T FindByPrimaryKey(object primaryKey)
        {
            T ret;
            return PrimaryKey2Row.TryGetValue(primaryKey, out ret) ? ret : default(T);
        }
    }
}
