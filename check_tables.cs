using System;
using System.Data.SQLite;

class Program
{
    static void Main()
    {
        string dbPath = @"c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\data\MDiceV2.db";
        
        using var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        
        // 获取所有表名
        using var command = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;", connection);
        using var reader = command.ExecuteReader();
        
        Console.WriteLine("=== 所有表名 ===");
        while (reader.Read())
        {
            string tableName = reader["name"].ToString() ?? "";
            Console.WriteLine($"表名: '{tableName}'");
        }
        
        // 检查 BasicSetting 表
        Console.WriteLine("\n=== 检查 BasicSetting 表 ===");
        try
        {
            using var checkCommand = new SQLiteCommand("SELECT COUNT(*) FROM BasicSetting", connection);
            var count = checkCommand.ExecuteScalar();
            Console.WriteLine($"BasicSetting 表存在，记录数: {count}");
            
            // 显示表结构
            using var schemaCommand = new SQLiteCommand("PRAGMA table_info(BasicSetting)", connection);
            using var schemaReader = schemaCommand.ExecuteReader();
            Console.WriteLine("BasicSetting 表结构:");
            while (schemaReader.Read())
            {
                Console.WriteLine($"  列: {schemaReader["name"]}, 类型: {schemaReader["type"]}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BasicSetting 表不存在或访问失败: {ex.Message}");
        }
        
        // 检查 Basicsetting 表（小写s）
        Console.WriteLine("\n=== 检查 Basicsetting 表 ===");
        try
        {
            using var checkCommand = new SQLiteCommand("SELECT COUNT(*) FROM Basicsetting", connection);
            var count = checkCommand.ExecuteScalar();
            Console.WriteLine($"Basicsetting 表存在，记录数: {count}");
            
            // 显示表结构
            using var schemaCommand = new SQLiteCommand("PRAGMA table_info(Basicsetting)", connection);
            using var schemaReader = schemaCommand.ExecuteReader();
            Console.WriteLine("Basicsetting 表结构:");
            while (schemaReader.Read())
            {
                Console.WriteLine($"  列: {schemaReader["name"]}, 类型: {schemaReader["type"]}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Basicsetting 表不存在或访问失败: {ex.Message}");
        }
    }
}
