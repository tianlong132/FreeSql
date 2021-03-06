﻿using SafeObjectPool;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FreeSql.Sqlite {

	class SqliteConnectionPool : ObjectPool<DbConnection> {

		internal Action availableHandler;
		internal Action unavailableHandler;

		public SqliteConnectionPool(string name, string connectionString, Action availableHandler, Action unavailableHandler) : base(null) {
			policy = new SqliteConnectionPoolPolicy {
				_pool = this,
				Name = name
			};
			this.Policy = policy;
			policy.ConnectionString = connectionString;

			this.availableHandler = availableHandler;
			this.unavailableHandler = unavailableHandler;
		}

		public void Return(Object<DbConnection> obj, Exception exception, bool isRecreate = false) {
			if (exception != null && exception is SQLiteException) {
				try { if ((obj.Value as SQLiteConnection).Ping() == false) obj.Value.OpenAndAttach(policy.Attaches); } catch { base.SetUnavailable(exception); }
			}
			base.Return(obj, isRecreate);
		}

		internal SqliteConnectionPoolPolicy policy;
	}

	class SqliteConnectionPoolPolicy : IPolicy<DbConnection> {

		internal SqliteConnectionPool _pool;
		public string Name { get; set; } = "Sqlite SQLiteConnection 对象池";
		public int PoolSize { get; set; } = 100;
		public TimeSpan SyncGetTimeout { get; set; } = TimeSpan.FromSeconds(10);
		public int AsyncGetCapacity { get; set; } = 10000;
		public bool IsThrowGetTimeoutException { get; set; } = true;
		public int CheckAvailableInterval { get; set; } = 5;
		public string[] Attaches = new string[0];

		private string _connectionString;
		public string ConnectionString {
			get => _connectionString;
			set {
				_connectionString = value ?? "";
				var m = Regex.Match(_connectionString, @"Max\s*pool\s*size\s*=\s*(\d+)", RegexOptions.IgnoreCase);
				if (m.Success == false || int.TryParse(m.Groups[1].Value, out var poolsize) == false || poolsize <= 0) poolsize = 100;
				PoolSize = poolsize;

				var att = Regex.Split(_connectionString, @"Attachs\s*=\s*", RegexOptions.IgnoreCase);
				if (att.Length == 2) {
					var idx = att[1].IndexOf(';');
					Attaches = (idx == -1 ? att[1] : att[1].Substring(0, idx)).Split(',');
				}

				var initConns = new Object<DbConnection>[poolsize];
				for (var a = 0; a < poolsize; a++) try { initConns[a] = _pool.Get(); } catch { }
				foreach (var conn in initConns) _pool.Return(conn);
			}
		}


		public bool OnCheckAvailable(Object<DbConnection> obj) {
			if ((obj.Value as SQLiteConnection).Ping() == false) obj.Value.OpenAndAttach(Attaches);
			return (obj.Value as SQLiteConnection).Ping();
		}

		public DbConnection OnCreate() {
			var conn = new SQLiteConnection(_connectionString);
			return conn;
		}

		public void OnDestroy(DbConnection obj) {
			if (obj.State != ConnectionState.Closed) obj.Close();
			obj.Dispose();
		}

		public void OnGet(Object<DbConnection> obj) {

			if (_pool.IsAvailable) {

				if (obj.Value.State != ConnectionState.Open || DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 && (obj.Value as SQLiteConnection).Ping() == false) {

					try {
						obj.Value.OpenAndAttach(Attaches);
					} catch (Exception ex) {
						if (_pool.SetUnavailable(ex) == true)
							throw new Exception($"【{this.Name}】状态不可用，等待后台检查程序恢复方可使用。{ex.Message}");
					}
				}
			}
		}

		async public Task OnGetAsync(Object<DbConnection> obj) {

			if (_pool.IsAvailable) {

				if (obj.Value.State != ConnectionState.Open || DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 && (obj.Value as SQLiteConnection).Ping() == false) {

					try {
						await obj.Value.OpenAndAttachAsync(Attaches);
					} catch (Exception ex) {
						if (_pool.SetUnavailable(ex) == true)
							throw new Exception($"【{this.Name}】状态不可用，等待后台检查程序恢复方可使用。{ex.Message}");
					}
				}
			}
		}

		public void OnGetTimeout() {

		}

		public void OnReturn(Object<DbConnection> obj) {

		}

		public void OnAvailable() {
			_pool.availableHandler?.Invoke();
		}

		public void OnUnavailable() {
			_pool.unavailableHandler?.Invoke();
		}
	}
	static class SqliteConnectionExtensions {

		public static bool Ping(this DbConnection that) {
			try {
				var cmd = that.CreateCommand();
				cmd.CommandText = "select 1";
				cmd.ExecuteNonQuery();
				return true;
			} catch {
				if (that.State != ConnectionState.Closed) try { that.Close(); } catch { }
				return false;
			}
		}

		public static void OpenAndAttach(this DbConnection that, string[] attach) {
			that.Open();

			if (attach?.Any() == true) {
				var sb = new StringBuilder();
				foreach(var att in attach)
					sb.Append($"attach database [{att}] as [{att.Split('.').First()}];\r\n");

				var cmd = that.CreateCommand();
				cmd.CommandText = sb.ToString();
				cmd.ExecuteNonQuery();
			}
		}
		async public static Task OpenAndAttachAsync(this DbConnection that, string[] attach) {
			await that.OpenAsync();

			if (attach?.Any() == true) {
				var sb = new StringBuilder();
				foreach (var att in attach)
					sb.Append($"attach database [{att}] as [{att.Split('.').First()}];\r\n");

				var cmd = that.CreateCommand();
				cmd.CommandText = sb.ToString();
				await cmd.ExecuteNonQueryAsync();
			}
		}
	}
}
