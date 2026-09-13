using System;
using System.Collections.Generic;
using Xunit;
using MDiceV2.Core;
using ValueType = MDiceV2.Core.MValueType;

namespace MDiceV2.Tests
{
    /// <summary>
    /// TypeSystem系统的单元测试
    /// 
    /// 测试范围：
    /// - TypeInfo基础操作（构造、转换、比较）
    /// - 8种基础类型（int, double, bool, string, null, dice, schema, array）
    /// - TypeRegistry全局注册表
    /// - Value → TypeInfo 映射
    /// - 序列化/反序列化
    /// </summary>
    [Collection("TypeSystem Tests")]
    public class TypeSystemTests
    {
        #region TypeInfo基础测试

        [Fact]
        [Category("Unit")]
        public void TestIntTypeInfoConstruction()
        {
            // 测试：获取int类型的TypeInfo
            // 验证：能正确创建、返回非null
            var intType = GetIntTypeInfo();
            
            Assert.NotNull(intType);
            Assert.Equal("int", intType.name);
        }

        [Fact]
        [Category("Unit")]
        public void TestDoubleTypeInfoConstruction()
        {
            // 测试：获取double类型的TypeInfo
            var doubleType = GetDoubleTypeInfo();
            
            Assert.NotNull(doubleType);
            Assert.Equal("double", doubleType.name);
        }

        [Fact]
        [Category("Unit")]
        public void TestBoolTypeInfoConstruction()
        {
            // 测试：获取bool类型的TypeInfo
            var boolType = GetBoolTypeInfo();
            
            Assert.NotNull(boolType);
            Assert.Equal("bool", boolType.name);
        }

        [Fact]
        [Category("Unit")]
        public void TestStringTypeInfoConstruction()
        {
            // 测试：获取string类型的TypeInfo
            var stringType = GetStringTypeInfo();
            
            Assert.NotNull(stringType);
            Assert.Equal("string", stringType.name);
        }

        [Fact]
        [Category("Unit")]
        public void TestNullTypeInfoConstruction()
        {
            // 测试：获取null类型的TypeInfo
            var nullType = GetNullTypeInfo();
            
            Assert.NotNull(nullType);
            Assert.Equal("null", nullType.name);
        }

        #endregion

        #region 类型转换测试

        [Theory]
        [Category("Unit")]
        [InlineData(0)]
        [InlineData(42)]
        [InlineData(-100)]
        [InlineData(int.MaxValue)]
        public void TestIntConversion(long intValue)
        {
            // 测试：整数类型的各种值转换
            var value = new Value(intValue);
            
            Assert.Equal(intValue, value.ToInt());
            Assert.Equal((double)intValue, value.ToDouble());
            Assert.Equal(intValue != 0, value.ToBool());
            Assert.Equal(intValue.ToString(), value.ToString());
        }

        [Theory]
        [Category("Unit")]
        [InlineData(0.0)]
        [InlineData(3.14159)]
        [InlineData(-2.71828)]
        [InlineData(double.MaxValue)]
        public void TestDoubleConversion(double doubleValue)
        {
            // 测试：浮点数类型的各种值转换
            var value = new Value(doubleValue);
            
            Assert.Equal((long)doubleValue, value.ToInt());
            Assert.Equal(doubleValue, value.ToDouble());
            Assert.Equal(doubleValue != 0.0, value.ToBool());
        }

        [Theory]
        [Category("Unit")]
        [InlineData(true)]
        [InlineData(false)]
        public void TestBoolConversion(bool boolValue)
        {
            // 测试：布尔类型的转换
            var value = new Value(boolValue);
            
            Assert.Equal(boolValue ? 1 : 0, value.ToInt());
            Assert.Equal(boolValue ? 1.0 : 0.0, value.ToDouble());
            Assert.Equal(boolValue, value.ToBool());
            Assert.Equal(boolValue ? "true" : "false", value.ToString());
        }

        [Theory]
        [Category("Unit")]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("123")]
        [InlineData("3.14")]
        public void TestStringConversion(string stringValue)
        {
            // 测试：字符串类型的转换
            var value = new Value(stringValue);
            
            Assert.Equal(stringValue, value.ToString());
            // int转换：字符串是否为纯数字
            if (long.TryParse(stringValue, out var intVal))
            {
                Assert.Equal(intVal, value.ToInt());
            }
        }

        #endregion

        #region 值类型判断测试

        [Fact]
        [Category("Unit")]
        public void TestValueTypeIdentification()
        {
            // 测试：Value类型识别是否正确
            var intVal = new Value(42);
            var doubleVal = new Value(3.14);
            var boolVal = new Value(true);
            var stringVal = new Value("test");
            var nullVal = new Value();
            
            Assert.Equal(ValueType.Int, intVal.GetType());
            Assert.Equal(ValueType.Double, doubleVal.GetType());
            Assert.Equal(ValueType.Bool, boolVal.GetType());
            Assert.Equal(ValueType.String, stringVal.GetType());
            Assert.Equal(ValueType.Null, nullVal.GetType());
        }

        #endregion

        #region TypeInfo映射测试

        [Fact]
        [Category("Unit")]
        public void TestValueGetTypeInfo()
        {
            // 测试：Value通过GetTypeInfo()获取对应的TypeInfo
            var intValue = new Value(42);
            var typeInfo = intValue.GetTypeInfo();
            
            Assert.NotNull(typeInfo);
            Assert.Equal("int", typeInfo.name);
        }

        [Fact]
        [Category("Unit")]
        public void TestValueGetTypeInfoForAllTypes()
        {
            // 测试：所有Value类型都能正确获取TypeInfo
            var values = new List<Value>
            {
                new Value(),                  // null
                new Value(42),                // int
                new Value(3.14),              // double
                new Value(true),              // bool
                new Value("test"),            // string
            };

            var expectedTypes = new List<string> { "null", "int", "double", "bool", "string" };
            
            for (int i = 0; i < values.Count; i++)
            {
                var typeInfo = values[i].GetTypeInfo();
                Assert.NotNull(typeInfo);
                Assert.Equal(expectedTypes[i], typeInfo.name);
            }
        }

        #endregion

        #region 等於和比較測試

        [Fact]
        [Category("Unit")]
        public void TestValueEquality()
        {
            // 测试：Value相等性比较
            var val1 = new Value(42);
            var val2 = new Value(42);
            var val3 = new Value(43);
            
            Assert.Equal(val1, val2);
            Assert.NotEqual(val1, val3);
        }

        [Fact]
        [Category("Unit")]
        public void TestValueComparison()
        {
            // 测试：Value大小比较
            var val1 = new Value(10);
            var val2 = new Value(20);
            var val3 = new Value(20);
            
            Assert.True(val1 < val2);
            Assert.True(val2 > val1);
            Assert.True(val2 >= val3);
            Assert.True(val2 <= val3);
        }

        #endregion

        #region 算數運算測試

        [Fact]
        [Category("Unit")]
        public void TestIntAddition()
        {
            // 测试：整数加法
            var a = new Value(10);
            var b = new Value(20);
            var result = a + b;
            
            Assert.Equal(30, result.ToInt());
        }

        [Fact]
        [Category("Unit")]
        public void TestIntSubtraction()
        {
            // 测试：整数减法
            var a = new Value(30);
            var b = new Value(10);
            var result = a - b;
            
            Assert.Equal(20, result.ToInt());
        }

        [Fact]
        [Category("Unit")]
        public void TestDoubleMultiplication()
        {
            // 测试：浮点数乘法
            var a = new Value(3.5);
            var b = new Value(2.0);
            var result = a * b;
            
            Assert.Equal(7.0, result.ToDouble(), 5);  // 5位精度
        }

        [Fact]
        [Category("Unit")]
        public void TestIntDivision()
        {
            // 测试：整数除法
            var a = new Value(20);
            var b = new Value(4);
            var result = a / b;
            
            Assert.Equal(5, result.ToInt());
        }

        #endregion

        #region Schema操作測試

        [Fact]
        [Category("Unit")]
        public void TestSchemaCreation()
        {
            // 测试：创建Schema值
            var schema = Value.CreateSchema();
            
            Assert.Equal(ValueType.Schema, schema.GetType());
        }

        [Fact]
        [Category("Unit")]
        public void TestSchemaFieldSet()
        {
            // 测试：设置Schema字段
            var schema = Value.CreateSchema();
            schema.SetField("name", new Value("test"));
            
            var value = schema.GetField("name");
            Assert.Equal("test", value.ToString());
        }

        #endregion

        #region Array操作測試

        [Fact]
        [Category("Unit")]
        public void TestArrayCreation()
        {
            // 测试：创建Array值
            var array = Value.CreateArray();
            
            Assert.Equal(ValueType.Array, array.GetType());
            Assert.Equal(0UL, array.ArraySize());
        }

        #endregion

        #region 向後兼容性測試

        [Fact]
        [Category("Unit")]
        public void TestValueBackwardCompatibility()
        {
            // 测试：新TypeInfo系统不破坏现有Value API
            var value = new Value(42);
            
            // 旧API应该继续工作
            Assert.Equal(value.GetType(), ValueType.Int);
            Assert.Equal(42, value.ToInt());
            Assert.Equal(42.0, value.ToDouble());
            Assert.Equal("42", value.ToString());
            
            // 新API也应该可用
            var typeInfo = value.GetTypeInfo();
            Assert.NotNull(typeInfo);
        }

        #endregion

        #region 性能基准测試

        [Fact]
        [Category("Performance")]
        public void TestValueCreationPerformance()
        {
            // 基准测试：Value创建性能
            const int iterations = 100000;
            var startTime = DateTime.UtcNow;
            
            for (int i = 0; i < iterations; i++)
            {
                var value = new Value(i);
            }
            
            var elapsed = DateTime.UtcNow - startTime;
            // 应该在合理时间内完成（< 1秒）
            Assert.True(elapsed.TotalSeconds < 1.0, 
                $"Creation of {iterations} values took {elapsed.TotalSeconds}s");
        }

        [Fact]
        [Category("Performance")]
        public void TestTypeInfoLookupPerformance()
        {
            // 基准测试：TypeInfo查询性能
            var value = new Value(42);
            const int iterations = 100000;
            var startTime = DateTime.UtcNow;
            
            for (int i = 0; i < iterations; i++)
            {
                var typeInfo = value.GetTypeInfo();
            }
            
            var elapsed = DateTime.UtcNow - startTime;
            // 应该在合理时间内完成（< 500ms）
            Assert.True(elapsed.TotalMilliseconds < 500, 
                $"Lookup of {iterations} TypeInfos took {elapsed.TotalMilliseconds}ms");
        }

        #endregion

        #region 輔助方法

        private ITypeInfo GetIntTypeInfo() => throw new NotImplementedException("需要实现C++互操作");
        private ITypeInfo GetDoubleTypeInfo() => throw new NotImplementedException("需要实现C++互操作");
        private ITypeInfo GetBoolTypeInfo() => throw new NotImplementedException("需要实现C++互操作");
        private ITypeInfo GetStringTypeInfo() => throw new NotImplementedException("需要实现C++互操作");
        private ITypeInfo GetNullTypeInfo() => throw new NotImplementedException("需要实现C++互操作");

        #endregion
    }

    /// <summary>
    /// Category标签用于测试分类
    /// </summary>
    public class CategoryAttribute : Attribute
    {
        public string Category { get; }
        
        public CategoryAttribute(string category)
        {
            Category = category;
        }
    }

    /// <summary>
    /// ITypeInfo接口定义
    /// 注：实际实现在C++代码中
    /// </summary>
    public interface ITypeInfo
    {
        string name { get; }
        string category { get; }
        // 更多属性...
    }
}
