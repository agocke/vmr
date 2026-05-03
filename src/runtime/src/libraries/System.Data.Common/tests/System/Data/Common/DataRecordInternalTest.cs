// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace System.Data.Common.Tests
{
    public class DataRecordInternalTest
    {
        private static Exception? CaptureException(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        [Fact]
        public void GetBytes_NegativeDataIndex_ThrowsIndexOutOfRangeException()
        {
            DataTable table = new DataTable();
            table.Columns.Add("Bytes", typeof(byte[]));
            table.Rows.Add(new byte[] { 1, 2, 3 });

            using DataTableReader reader = table.CreateDataReader();
            reader.Read();

            byte[] buffer = new byte[3];

            Exception? exception = CaptureException(() => reader.GetBytes(0, Int64.MinValue, buffer, 0, buffer.Length));
            Assert.NotNull(exception);
            Assert.True(exception is IndexOutOfRangeException or ArgumentOutOfRangeException, $"Unexpected exception type: {exception.GetType()}");
        }

        [Fact]
        public void GetChars_NegativeDataIndex_ThrowsIndexOutOfRangeException()
        {
            DataTable table = new DataTable();
            table.Columns.Add("Chars", typeof(char[]));
            table.Rows.Add(new char[] { 'a', 'b', 'c' });

            using DataTableReader reader = table.CreateDataReader();
            reader.Read();

            char[] buffer = new char[3];

            Exception? exception = CaptureException(() => reader.GetChars(0, Int64.MinValue, buffer, 0, buffer.Length));
            Assert.NotNull(exception);
            Assert.True(exception is IndexOutOfRangeException or ArgumentOutOfRangeException, $"Unexpected exception type: {exception.GetType()}");
        }
    }
}
