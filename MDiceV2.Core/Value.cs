using System;
using System.Collections.Generic;

namespace MDiceV2.Core
{
    /// <summary>
    /// MValueType 枚举 - 表示 Value 的类型（重命名以避免�?System.MValueType 冲突�?
    /// </summary>
    public enum MValueType
    {
        Null = 0,
        Int = 1,
        Double = 2,
        Bool = 3,
        String = 4,
        Schema = 5,
        Array = 6,
    }

    /// <summary>
    /// Value - 通用的值类型容器
    /// 支持 Int, Double, Bool, String, Null, Schema, Array 等类型
    /// </summary>
    public class Value : IEquatable<Value>, IComparable<Value>
    {
        private MValueType _type = MValueType.Null;
        private object? _value = null;

        // ===================== Schema 操作 =====================
        public void SetField(string key, Value val)
        {
            if (_type != MValueType.Schema)
                throw new InvalidOperationException("Not a schema value");
            var dict = (Dictionary<string, Value>)_value!;
            dict[key] = val;
        }

        public Value GetField(string key)
        {
            if (_type != MValueType.Schema)
                throw new InvalidOperationException("Not a schema value");
            var dict = (Dictionary<string, Value>)_value!;
            return dict.TryGetValue(key, out var val) ? val : new Value();
        }

        public bool ContainsField(string key)
        {
            if (_type != MValueType.Schema)
                throw new InvalidOperationException("Not a schema value");
            var dict = (Dictionary<string, Value>)_value!;
            return dict.ContainsKey(key);
        }

        public IEnumerable<string> GetFieldKeys()
        {
            if (_type != MValueType.Schema)
                throw new InvalidOperationException("Not a schema value");
            var dict = (Dictionary<string, Value>)_value!;
            return dict.Keys;
        }

        // ===================== Array 操作 =====================
        public void Add(Value val)
        {
            if (_type != MValueType.Array)
                throw new InvalidOperationException("Not an array value");
            var list = (List<Value>)_value!;
            list.Add(val);
        }

        public Value this[int index]
        {
            get
            {
                if (_type != MValueType.Array)
                    throw new InvalidOperationException("Not an array value");
                var list = (List<Value>)_value!;
                return (index >= 0 && index < list.Count) ? list[index] : new Value();
            }
            set
            {
                if (_type != MValueType.Array)
                    throw new InvalidOperationException("Not an array value");
                var list = (List<Value>)_value!;
                if (index >= 0 && index < list.Count)
                    list[index] = value;
                else
                    throw new IndexOutOfRangeException();
            }
        }

        public ulong ArraySize()
        {
            if (_type != MValueType.Array)
                throw new InvalidOperationException("Not an array value");
            var list = (List<Value>)_value!;
            return (ulong)list.Count;
        }

        public List<Value> ToArray()
        {
            if (_type != MValueType.Array)
                throw new InvalidOperationException("Not an array value");
            return new List<Value>((List<Value>)_value!);
        }

        // ===================== 算术操作符重载 =====================
        public static Value operator +(Value a, Value b)
        {
            if (a.IsInt() && b.IsInt())
                return new Value(a.ToInt() + b.ToInt());
            if ((a.IsDouble() || b.IsDouble()))
                return new Value(a.ToDouble() + b.ToDouble());
            if (a.IsString() || b.IsString())
                return new Value(a.ToString() + b.ToString());
            throw new InvalidOperationException("Unsupported types for +");
        }

        public static Value operator -(Value a, Value b)
        {
            if (a.IsInt() && b.IsInt())
                return new Value(a.ToInt() - b.ToInt());
            if ((a.IsDouble() || b.IsDouble()))
                return new Value(a.ToDouble() - b.ToDouble());
            throw new InvalidOperationException("Unsupported types for -");
        }

        public static Value operator *(Value a, Value b)
        {
            if (a.IsInt() && b.IsInt())
                return new Value(a.ToInt() * b.ToInt());
            if ((a.IsDouble() || b.IsDouble()))
                return new Value(a.ToDouble() * b.ToDouble());
            throw new InvalidOperationException("Unsupported types for *");
        }

        public static Value operator /(Value a, Value b)
        {
            if (a.IsInt() && b.IsInt())
                return new Value(b.ToInt() == 0 ? 0 : a.ToInt() / b.ToInt());
            if ((a.IsDouble() || b.IsDouble()))
                return new Value(b.ToDouble() == 0.0 ? 0.0 : a.ToDouble() / b.ToDouble());
            throw new InvalidOperationException("Unsupported types for /");
        }
        // ...existing code...

        // =====================================================
        // 构造函�?
        // =====================================================

        /// <summary>
        /// 创建 Null 类型�?Value
        /// </summary>
        public Value()
        {
            _type = MValueType.Null;
            _value = null;
        }


        /// <summary>
        /// 创建 Int 类型 Value
        /// </summary>
        public Value(int intValue)
        {
            _type = MValueType.Int;
            _value = (long)intValue;
        }
        public Value(long intValue)
        {
            _type = MValueType.Int;
            _value = intValue;
        }
        /// <summary>
        /// 创建 Double 类型 Value
        /// </summary>
        public Value(float doubleValue)
        {
            _type = MValueType.Double;
            _value = (double)doubleValue;
        }
        public Value(double doubleValue)
        {
            _type = MValueType.Double;
            _value = doubleValue;
        }
        /// <summary>
        /// 创建 Bool 类型 Value
        /// </summary>
        public Value(bool boolValue)
        {
            _type = MValueType.Bool;
            _value = boolValue;
        }
        /// <summary>
        /// 创建 String 类型 Value
        /// </summary>
        public Value(string stringValue)
        {
            _type = MValueType.String;
            _value = stringValue ?? "";
        }

        /// <summary>
        /// 创建 Schema 类型�?Value
        /// </summary>
        public Value(Dictionary<string, Value> schemaValue)
        {
            _type = MValueType.Schema;
            _value = schemaValue;
        }

        /// <summary>
        /// 创建 Array 类型�?Value
        /// </summary>
        public Value(List<Value> arrayValue)
        {
            _type = MValueType.Array;
            _value = arrayValue;
        }

        // =====================================================
        // 工厂方法
        // =====================================================

        /// <summary>
        /// 创建 Schema 类型�?Value
        /// </summary>
        public static Value CreateSchema()
        {
            return new Value(new Dictionary<string, Value>());
        }

        /// <summary>
        /// 创建 Array 类型�?Value
        /// </summary>
        public static Value CreateArray()
        {
            return new Value(new List<Value>());
        }

        // =====================================================
        // 类型判断方法
        // =====================================================

        /// <summary>
        /// 获取 Value 的类�?
        /// </summary>
        public MValueType GetType()
        {
            return _type;
        }

        /// <summary>
        /// 判断是否�?Null
        /// </summary>
        public bool IsNull() => _type == MValueType.Null;

        /// <summary>
        /// 判断是否�?Int
        /// </summary>
        public bool IsInt() => _type == MValueType.Int;

        /// <summary>
        /// 判断是否�?Double
        /// </summary>
        public bool IsDouble() => _type == MValueType.Double;

        /// <summary>
        /// 判断是否�?Bool
        /// </summary>
        public bool IsBool() => _type == MValueType.Bool;

        /// <summary>
        /// 判断是否�?String
        /// </summary>
        public bool IsString() => _type == MValueType.String;

        /// <summary>
        /// 判断是否�?Schema
        /// </summary>
        public bool IsSchema() => _type == MValueType.Schema;

        /// <summary>
        /// 判断是否�?Array
        /// </summary>
        public bool IsArray() => _type == MValueType.Array;

        // =====================================================
        // 类型转换方法
        // =====================================================

        /// <summary>
        /// 转换�?Int
        /// </summary>
        public long ToInt()
        {
            return _type switch
            {
                MValueType.Int => (long)(_value ?? 0L),
                MValueType.Double => (long)((double?)_value ?? 0.0),
                MValueType.Bool => (bool?)_value ?? false ? 1L : 0L,
                MValueType.String => long.TryParse((string?)_value ?? "", out var result) ? result : 0L,
                MValueType.Null => 0L,
                _ => 0L,
            };
        }

        /// <summary>
        /// 转换�?Double
        /// </summary>
        public double ToDouble()
        {
            return _type switch
            {
                MValueType.Int => (double?)((long?)_value ?? 0L) ?? 0.0,
                MValueType.Double => (double?)_value ?? 0.0,
                MValueType.Bool => (bool?)_value ?? false ? 1.0 : 0.0,
                MValueType.String => double.TryParse((string?)_value ?? "", out var result) ? result : 0.0,
                MValueType.Null => 0.0,
                _ => 0.0,
            };
        }

        /// <summary>
        /// 转换�?Bool
        /// </summary>
        public bool ToBool()
        {
            return _type switch
            {
                MValueType.Int => ((long?)_value ?? 0L) != 0,
                MValueType.Double => ((double?)_value ?? 0.0) != 0.0,
                MValueType.Bool => (bool?)_value ?? false,
                MValueType.String => !string.IsNullOrEmpty((string?)_value),
                MValueType.Null => false,
                _ => false,
            };
        }

        /// <summary>
        /// 转换�?String
        /// </summary>
        public override string ToString()
        {
            return _type switch
            {
                MValueType.Int => ((long?)_value ?? 0L).ToString(),
                MValueType.Double => ((double?)_value ?? 0.0).ToString("G"),
                MValueType.Bool => ((bool?)_value ?? false) ? "true" : "false",
                MValueType.String => (string?)_value ?? "",
                MValueType.Null => "null",
                _ => "",
            };
        }

        // =====================================================
        // TypeInfo 获取
        // =====================================================

        /// <summary>
        /// 获取 Value 对应�?TypeInfo
        /// </summary>
        public TypeInfo GetTypeInfo()
        {
            return _type switch
            {
                MValueType.Int => new TypeInfo { name = "int", kind = TypeKind.Int },
                MValueType.Double => new TypeInfo { name = "double", kind = TypeKind.Double },
                MValueType.Bool => new TypeInfo { name = "bool", kind = TypeKind.Bool },
                MValueType.String => new TypeInfo { name = "string", kind = TypeKind.String },
                MValueType.Null => new TypeInfo { name = "null", kind = TypeKind.Null },
                MValueType.Schema => new TypeInfo { name = "schema", kind = TypeKind.Schema },
                MValueType.Array => new TypeInfo { name = "array", kind = TypeKind.Array },
                _ => new TypeInfo { name = "unknown", kind = TypeKind.Null },
            };
        }

        // =====================================================
        // 相等性比�?
        // =====================================================

        /// <summary>
        /// 相等性比�?
        /// </summary>
        public override bool Equals(object? obj)
        {
            if (obj is Value other)
            {
                return Equals(other);
            }
            return false;
        }

        /// <summary>
        /// 相等性比�?
        /// </summary>
        public bool Equals(Value? other)
        {
            if (other == null)
                return false;

            if (_type != other._type)
                return false;

            return _type switch
            {
                MValueType.Int => ((long?)_value ?? 0L) == ((long?)other._value ?? 0L),
                MValueType.Double => ((double?)_value ?? 0.0) == ((double?)other._value ?? 0.0),
                MValueType.Bool => ((bool?)_value ?? false) == ((bool?)other._value ?? false),
                MValueType.String => ((string?)_value ?? "") == ((string?)other._value ?? ""),
                MValueType.Null => true,
                _ => false,
            };
        }

        /// <summary>
        /// 哈希�?
        /// </summary>
        public override int GetHashCode()
        {
            return _type switch
            {
                MValueType.Int => ((long?)_value ?? 0L).GetHashCode(),
                MValueType.Double => ((double?)_value ?? 0.0).GetHashCode(),
                MValueType.Bool => ((bool?)_value ?? false).GetHashCode(),
                MValueType.String => ((string?)_value ?? "").GetHashCode(),
                MValueType.Null => 0,
                _ => 0,
            };
        }

        // =====================================================
        // 大小比较
        // =====================================================

        /// <summary>
        /// 大小比较
        /// </summary>
        public int CompareTo(Value? other)
        {
            if (other == null)
                return 1;

            // 统一转换�?Double 比较
            double thisValue = ToDouble();
            double otherValue = other.ToDouble();

            return thisValue.CompareTo(otherValue);
        }

        /// <summary>
        /// 小于运算�?
        /// </summary>
        public static bool operator <(Value a, Value b)
        {
            return a.CompareTo(b) < 0;
        }

        /// <summary>
        /// 大于运算�?
        /// </summary>
        public static bool operator >(Value a, Value b)
        {
            return a.CompareTo(b) > 0;
        }

        /// <summary>
        /// 小于等于运算�?
        /// </summary>
        public static bool operator <=(Value a, Value b)
        {
            return a.CompareTo(b) <= 0;
        }

        /// <summary>
        /// 大于等于运算�?
        /// </summary>
        public static bool operator >=(Value a, Value b)
        {
            return a.CompareTo(b) >= 0;
        }

        /// <summary>
        /// 相等运算�?
        /// </summary>
        public static bool operator ==(Value? a, Value? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (ReferenceEquals(a, null) || ReferenceEquals(b, null))
                return false;
            return a.Equals(b);
        }

        /// <summary>
        /// 不相等运算符
        /// </summary>
        public static bool operator !=(Value? a, Value? b)
        {
            return !(a == b);
        }
    }

    /// <summary>
    /// TypeInfo �?- 表示类型信息
    /// </summary>
    public class TypeInfo
    {
        /// <summary>
        /// 类型名称
        /// </summary>
        public string name { get; set; } = "unknown";

        /// <summary>
        /// 类型分类
        /// </summary>
        public TypeKind kind { get; set; } = TypeKind.Null;
    }

    /// <summary>
    /// TypeKind 枚举 - 类型分类
    /// </summary>
    public enum TypeKind
    {
        Null = 0,
        Int = 1,
        Double = 2,
        Bool = 3,
        String = 4,
        Schema = 5,
        Array = 6,
    }

    /// <summary>
    /// TypeRegistry �?- 全局类型注册�?
    /// </summary>
    public static class TypeRegistry
    {
        private static Dictionary<string, TypeInfo> _registry = new();

        /// <summary>
        /// 获取 Int 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetIntTypeInfo() => new TypeInfo { name = "int", kind = TypeKind.Int };

        /// <summary>
        /// 获取 Double 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetDoubleTypeInfo() => new TypeInfo { name = "double", kind = TypeKind.Double };

        /// <summary>
        /// 获取 Bool 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetBoolTypeInfo() => new TypeInfo { name = "bool", kind = TypeKind.Bool };

        /// <summary>
        /// 获取 String 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetStringTypeInfo() => new TypeInfo { name = "string", kind = TypeKind.String };

        /// <summary>
        /// 获取 Null 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetNullTypeInfo() => new TypeInfo { name = "null", kind = TypeKind.Null };

        /// <summary>
        /// 获取 Schema 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetSchemaTypeInfo() => new TypeInfo { name = "schema", kind = TypeKind.Schema };

        /// <summary>
        /// 获取 Array 类型�?TypeInfo
        /// </summary>
        public static TypeInfo GetArrayTypeInfo() => new TypeInfo { name = "array", kind = TypeKind.Array };
    }
}
