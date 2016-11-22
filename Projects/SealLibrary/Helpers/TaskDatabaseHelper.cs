﻿using Seal.Model;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Seal.Helpers
{
    public delegate string CustomGetTableCreateCommand(DataTable table);

    public delegate string CustomGetTableColumnNames(DataTable table);
    public delegate string CustomGetTableColumnName(DataColumn col);
    public delegate string CustomGetTableColumnType(DataColumn col);

    public delegate string CustomGetTableColumnValues(DataRow row, string dateTimeFormat);
    public delegate string CustomGetTableColumnValue(DataRow row, DataColumn col, string datetimeFormat);

    public delegate DataTable CustomLoadDataTable(string connectionString, string sql);
    public delegate DataTable CustomLoadDataTableFromExcel(string excelPath, string tabName = "");
    public delegate DataTable CustomLoadDataTableFromCSV(string csvPath, char? separator = null);

    public class TaskDatabaseHelper
    {
        //Config, may be overwritten
        public string ColumnCharType = "";
        public string ColumnNumericType = "";
        public string ColumnIntegerType = "";
        public string ColumnDateTimeType = "";
        public string InsertStartCommand = "";
        public string InsertEndCommand = "";
        public int ColumnCharLength = 0; //0= means auto size
        public int InsertBurstSize = 500;
        public string ExcelOdbcDriver = "Driver={{Microsoft Excel Driver (*.xls, *.xlsx, *.xlsm, *.xlsb)}};DBQ={0}";
        public Encoding DefaultEncoding = Encoding.Default;
        public bool TrimText = true;

        public bool DebugMode = false;
        public StringBuilder DebugLog = new StringBuilder();
        public int SelectTimeout = 0;

        public CustomGetTableCreateCommand MyGetTableCreateCommand = null;

        public CustomGetTableColumnNames MyGetTableColumnNames = null;
        public CustomGetTableColumnName MyGetTableColumnName = null;
        public CustomGetTableColumnType MyGetTableColumnType = null;

        public CustomGetTableColumnValues MyGetTableColumnValues = null;
        public CustomGetTableColumnValue MyGetTableColumnValue = null;

        public CustomLoadDataTable MyLoadDataTable = null;
        public CustomLoadDataTableFromExcel MyLoadDataTableFromExcel = null;
        public CustomLoadDataTableFromCSV MyLoadDataTableFromCSV = null;


        string _defaultColumnCharType = "";
        string _defaultColumnIntegerType = "";
        string _defaultColumnNumericType = "";
        string _defaultColumnDateTimeType = "";
        string _defaultInsertStartCommand = "";
        string _defaultInsertEndCommand = "";

        public string CleanName(string name)
        {
            return name.Replace("-", "_").Replace(" ", "_").Replace("\"", "_").Replace("'", "_").Replace("[", "_").Replace("]", "_").Replace("/", "_").Replace("%", "_").Replace("(", "_").Replace(")", "_");
        }

        public string GetInsertCommand(string sql)
        {
            return Helper.IfNullOrEmpty(InsertStartCommand, _defaultInsertStartCommand) + " " + sql + " " + Helper.IfNullOrEmpty(InsertEndCommand, _defaultInsertEndCommand);
        }

        public DataTable OdbcLoadDataTable(string odbcConnectionString, string sql)
        {
            DataTable table = new DataTable();
            using (OdbcConnection connection = new OdbcConnection(odbcConnectionString))
            {
                connection.Open();
                OdbcDataAdapter adapter = new OdbcDataAdapter(sql, connection);
                adapter.SelectCommand.CommandTimeout = SelectTimeout;
                adapter.Fill(table);
                connection.Close();
            }
            return table;
        }

        public DataTable LoadDataTable(string connectionString, string sql)
        {
            if (MyLoadDataTable != null) return MyLoadDataTable(connectionString, sql);
            
            DataTable table = new DataTable();
            DbDataAdapter adapter = null;
            var connection = Helper.DbConnectionFromConnectionString(connectionString);
            connection.Open();
            if (connection is OdbcConnection) adapter = new OdbcDataAdapter(sql, (OdbcConnection)connection);
            else adapter = new OleDbDataAdapter(sql, (OleDbConnection)connection);
            adapter.SelectCommand.CommandTimeout = SelectTimeout;
            adapter.Fill(table);
            return table;
        }

        public DataTable LoadDataTableFromExcel(string excelPath, string tabName = "")
        {
            if (MyLoadDataTableFromExcel != null) return MyLoadDataTableFromExcel(excelPath, tabName);

            //Copy the Excel file if it is open...
            FileHelper.PurgeTempApplicationDirectory();
            string newPath = FileHelper.GetTempUniqueFileName(excelPath);
            File.Copy(excelPath, newPath, true);
            File.SetLastWriteTime(newPath, DateTime.Now);

            string connectionString = string.Format(ExcelOdbcDriver, newPath);
            string sql = string.Format("select * from [{0}$]", Helper.IfNullOrEmpty(tabName, "Sheet1"));
            return OdbcLoadDataTable(connectionString, sql);
        }

        public DataTable LoadDataTableFromCSV(string csvPath, char? separator = null)
        {
            if (MyLoadDataTableFromCSV != null) return MyLoadDataTableFromCSV(csvPath, separator);
            
            DataTable result = null;
            bool isHeader = true;
            Regex regexp = null;
            List<string> languages = new List<string>();

            string[] lines = null;
            try
            {
                lines = File.ReadAllLines(csvPath, DefaultEncoding);
            }
            catch
            {
                //Try by copying the file...
                string newPath = FileHelper.GetTempUniqueFileName(csvPath);
                File.Copy(csvPath, newPath);
                lines = File.ReadAllLines(newPath, DefaultEncoding);
                FileHelper.PurgeTempApplicationDirectory();
            }
            

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (regexp == null)
                {
                    string exp = "(?<=^|,)(\"(?:[^\"]|\"\")*\"|[^,]*)";
                    if (separator == null)
                    {
                        //use the first line to determine the separator between , and ;
                        separator = ',';
                        if (line.Split(';').Length > line.Split(',').Length) separator = ';';
                    }
                    if (separator != ',') exp = exp.Replace(',', separator.Value);
                    regexp = new Regex(exp);
                }

                MatchCollection collection = regexp.Matches(line);
                if (isHeader)
                {
                    result = new DataTable();
                    for (int i = 0; i < collection.Count; i++)
                    {
                        result.Columns.Add(new DataColumn(ExcelHelper.FromCsv(collection[i].Value), typeof(string)));
                    }
                    isHeader = false;
                }
                else
                {
                    var row = result.Rows.Add();
                    for (int i = 0; i < collection.Count && i < result.Columns.Count; i++)
                    {
                        row[i] = ExcelHelper.FromCsv(collection[i].Value);
                    }
                }
            }

            return result;
        }

        public void SetDatabaseDefaultConfiguration(DatabaseType type)
        {
            if (type == DatabaseType.Oracle)
            {
                _defaultColumnCharType = "varchar2";
                _defaultColumnNumericType = "number(18,3)";
                _defaultColumnIntegerType = "number(12)";
                _defaultColumnDateTimeType = "date";
                _defaultInsertStartCommand = "begin";
                _defaultInsertEndCommand = "end;";
            }
            else
            {
                //Default, tested on SQLServer...
                _defaultColumnCharType = "varchar";
                _defaultColumnNumericType = "numeric(18,3)";
                _defaultColumnIntegerType = "int";
                _defaultColumnDateTimeType = "datetime";
                _defaultInsertStartCommand = "";
                _defaultInsertEndCommand = "";
            }
        }


        public void ExecuteCommand(DbCommand command)
        {
            if (DebugMode) DebugLog.AppendLine("Executing SQL Command\r\n" + command.CommandText);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception(string.Format("Error executing SQL:\r\n{0}\r\n\r\n{1}", command.CommandText, ex.Message));
            }
        }

        public void CreateTable(DbCommand command, DataTable table)
        {
            try
            {
                command.CommandText = string.Format("drop table {0}", CleanName(table.TableName));
                ExecuteCommand(command);
            }
            catch { }
            command.CommandText = GetTableCreateCommand(table);
            ExecuteCommand(command);
        }

        public void InsertTable(DbCommand command, DataTable table, string dateTimeFormat, bool deleteFirst)
        {
            DbTransaction transaction = command.Connection.BeginTransaction();
            int cnt = 0;
            try
            {
                command.Transaction = transaction;
                if (deleteFirst)
                {
                    command.CommandText = string.Format("delete from {0}", CleanName(table.TableName));
                    ExecuteCommand(command);
                }

                StringBuilder sql = new StringBuilder("");
                string sqlTemplate = string.Format("insert into {0} ({1})", CleanName(table.TableName), GetTableColumnNames(table)) + " values ({0});";
                foreach (DataRow row in table.Rows)
                {
                    sql.AppendFormat(sqlTemplate, GetTableColumnValues(row, dateTimeFormat));
                    cnt++;
                    if (cnt % InsertBurstSize == 0)
                    {
                        command.CommandText = GetInsertCommand(sql.ToString());
                        ExecuteCommand(command);
                        sql = new StringBuilder("");
                    }
                }

                if (sql.Length != 0)
                {
                    command.CommandText = GetInsertCommand(sql.ToString());
                    ExecuteCommand(command);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public string RootGetTableCreateCommand(DataTable table)
        {
            StringBuilder result = new StringBuilder();
            foreach (DataColumn col in table.Columns)
            {
                if (result.Length > 0) result.Append(',');
                result.AppendFormat("{0} ", GetTableColumnName(col));
                result.Append(GetTableColumnType(col));
                result.Append(" NULL");
            }

            return string.Format("CREATE TABLE {0} ({1})", CleanName(table.TableName), result);
        }


        public string GetTableCreateCommand(DataTable table)
        {
            if (MyGetTableCreateCommand != null) return MyGetTableCreateCommand(table);
            return RootGetTableCreateCommand(table);
        }

        public string RootGetTableColumnNames(DataTable table)
        {
            StringBuilder result = new StringBuilder();
            foreach (DataColumn col in table.Columns)
            {
                if (result.Length > 0) result.Append(',');
                result.AppendFormat("{0}", GetTableColumnName(col));
            }
            return result.ToString();
        }

        public string GetTableColumnNames(DataTable table)
        {
            if (MyGetTableColumnNames != null) return MyGetTableColumnNames(table);
            return RootGetTableColumnNames(table);
        }

        public string RootGetTableColumnName(DataColumn col)
        {
            return CleanName(col.ColumnName);
        }

        public string GetTableColumnName(DataColumn col)
        {
            if (MyGetTableColumnName != null) return MyGetTableColumnName(col);
            return RootGetTableColumnName(col);
        }

        public string RootGetTableColumnType(DataColumn col)
        {
            StringBuilder result = new StringBuilder();
            if (col.DataType.Name == "Int32" || col.DataType.Name == "Integer" || col.DataType.Name == "Double" || col.DataType.Name == "Decimal" || col.DataType.Name == "Number")
            {
                //Check for integer
                bool isInteger = true;
                foreach (DataRow row in col.Table.Rows)
                {
                    int a;
                    if (!row.IsNull(col) && !int.TryParse(row[col].ToString(), out a))
                    {
                        isInteger = false;
                        break;
                    }
                }

                if (isInteger) result.Append(Helper.IfNullOrEmpty(ColumnIntegerType, _defaultColumnIntegerType));
                else result.Append(Helper.IfNullOrEmpty(ColumnNumericType, _defaultColumnNumericType));
            }
            else if (col.DataType.Name == "DateTime")
            {
                result.Append(Helper.IfNullOrEmpty(ColumnDateTimeType, _defaultColumnDateTimeType));
            }
            else
            {
                int len = col.MaxLength;
                if (len <= 0) len = ColumnCharLength;
                if (ColumnCharLength <= 0)
                {
                    //auto size
                    len = 1;
                    foreach (DataRow row in col.Table.Rows)
                    {
                        if (row[col].ToString().Length > len) len = row[col].ToString().Length + 1;
                    }
                }
                result.AppendFormat("{0}({1})", Helper.IfNullOrEmpty(ColumnCharType, _defaultColumnCharType), len);
            }
            return result.ToString();
        }

        public string GetTableColumnType(DataColumn col)
        {
            if (MyGetTableColumnType != null) return MyGetTableColumnType(col);
            return RootGetTableColumnType(col);
        }

        public string RootGetTableColumnValues(DataRow row, string dateTimeFormat)
        {
            StringBuilder result = new StringBuilder();
            foreach (DataColumn col in row.Table.Columns)
            {
                if (result.Length > 0) result.Append(',');
                result.Append(GetTableColumnValue(row, col, dateTimeFormat));
            }
            return result.ToString();
        }


        public string GetTableColumnValues(DataRow row, string dateTimeFormat)
        {
            if (MyGetTableColumnValues != null) return MyGetTableColumnValues(row, dateTimeFormat);
            return RootGetTableColumnValues(row, dateTimeFormat);
        }

        public string RootGetTableColumnValue(DataRow row, DataColumn col, string dateTimeFormat)
        {
            StringBuilder result = new StringBuilder();
            if (row.IsNull(col))
            {
                result.Append("NULL");
            }
            else if (col.DataType.Name == "Integer" || col.DataType.Name == "Double" || col.DataType.Name == "Decimal" || col.DataType.Name == "Number")
            {
                result.AppendFormat(row[col].ToString().Replace(',', '.'));
            }
            else if (col.DataType.Name == "DateTime" || col.DataType.Name == "Date")
            {
                result.Append(Helper.QuoteSingle(((DateTime)row[col]).ToString(dateTimeFormat)));
            }
            else
            {
                string res = row[col].ToString().Replace("\r", " ").Replace("\n", " ");
                if (TrimText) res = res.Trim();
                result.Append(Helper.QuoteSingle(res));
            }

            return result.ToString();
        }

        public string GetTableColumnValue(DataRow row, DataColumn col, string dateTimeFormat)
        {
            if (MyGetTableColumnValue != null) return MyGetTableColumnValue(row, col, dateTimeFormat);
            return RootGetTableColumnValue(row, col, dateTimeFormat);
        }

        public bool AreTablesIdentical(DataTable checkTable1, DataTable checkTable2)
        {
            bool result = true;
            if (checkTable1.Rows.Count != checkTable2.Rows.Count || checkTable1.Columns.Count != checkTable2.Columns.Count) result = false;
            if (checkTable1.Rows.Count != checkTable2.Rows.Count) result = false;
            else
            {
                for (int i = 0; i < checkTable1.Rows.Count && result; i++)
                {
                    for (int j = 0; j < checkTable1.Columns.Count && result; j++)
                    {
                        if (checkTable1.Rows[i][j].ToString() != checkTable2.Rows[i][j].ToString()) result = false;
                    }
                }
            }
            return result;
        }
    }
}
