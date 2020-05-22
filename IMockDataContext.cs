using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Linq;
using System.Data.Linq.Mapping;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Mocking {
	public interface IMockDataContext {
		MetaModel Mapping { get; }
		ITable<TEntity> GetTable<TEntity>() where TEntity : class;
		ITable GetTable(Type type);
	}

	public interface IMockTable {
		void SubmitChanges();
	}

	public class MemoryTable<TEntity> : IMockTable, ITable, ITable<TEntity> where TEntity : class {
		public Boolean IsReadOnly => false;

		private readonly IMockDataContext _context;
		private readonly MetaTable _metaTable;

		private readonly List<TEntity> _stored = new List<TEntity>();
		private readonly List<TEntity> _insert = new List<TEntity>();
		private readonly List<TEntity> _delete = new List<TEntity>();

		private Int32 _nextId = 1;

		private readonly Object lockObj = new Object();

		public MemoryTable(IMockDataContext context) {
			_context = context;
			_metaTable = _context.Mapping.GetTable(typeof(TEntity));
		}

		public void SubmitChanges() {
			System.Collections.ObjectModel.ReadOnlyCollection<MetaDataMember> ids = _metaTable.RowType.IdentityMembers;
			IEnumerable<MetaDataMember> autoIds = ids.Where(id => id.IsDbGenerated);
			if (autoIds.Count() > 1) throw new NotImplementedException("Can't handle more than one auto-generated identity column!");

			lock (lockObj) {
				foreach (TEntity insert in _insert) {
					if (autoIds.Any())
						((PropertyInfo)autoIds.First().Member).SetValue(insert, _nextId++, null);

					_stored.Add(insert);
				}

				foreach (TEntity entity in _delete) _stored.Remove(entity);

				_insert.Clear();
			}
		}

		public void Attach(TEntity entity) => throw new NotImplementedException();

		public void DeleteOnSubmit(TEntity entity) => _delete.Add(entity);

		public void InsertOnSubmit(TEntity entity) {
			lock (lockObj) {
				var rowType = _metaTable.RowType;

				// insert linked Objects
				foreach (MetaAssociation fk_assoc in rowType.Associations.Where(a => a.IsForeignKey)) {
					PropertyInfo prop = (PropertyInfo)fk_assoc.ThisMember.Member;
					Object fk_entity = prop.GetValue(entity, null);
					if (fk_entity != null) {
						if (fk_assoc.OtherKey.Count > 1) continue; // don't know how to handle more than one key!
						String key = fk_assoc.OtherKey[0].MappedName;

						// update the FK property
						// not easy to do, because the entity reference is already set
						if (fk_assoc.OtherMember == null) {
							// no child property
							// have to get a bit evil and use reflection on private field in this case
							((INotifyPropertyChanged)fk_entity).PropertyChanged += (sender, e) => {
								if (e.PropertyName == key) {
									String thisKeyName = fk_assoc.ThisKey[0].Name;
									FieldInfo idField = entity.GetType().GetField("_" + thisKeyName, BindingFlags.Instance | BindingFlags.NonPublic);

									PropertyInfo otherKey = fk_entity.GetType().GetProperty(key);
									Object otherKeyVal = otherKey.GetValue(fk_entity, null);

									idField.SetValue(entity, otherKeyVal);
								}
							};
						} else {
							((INotifyPropertyChanged)fk_entity).PropertyChanged += (sender, e) => {
								if (e.PropertyName == key) {
									prop.SetValue(entity, null, null);
									prop.SetValue(entity, fk_entity, null);
								}
							};
						}
						_context.GetTable(fk_entity.GetType()).InsertOnSubmit(fk_entity);
					}
				}

				_insert.Add(entity);
			}
		}

		public IEnumerator<TEntity> GetEnumerator() => _stored.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _stored.GetEnumerator();

		public Type ElementType => typeof(TEntity);

		public Expression Expression => _stored.AsQueryable().Expression;

		public IQueryProvider Provider => new CaseInsensitiveQueries(_stored.AsQueryable().Provider);

		#region ITable members
		public void Attach(Object entity, Object original) => throw new NotImplementedException();

		public void Attach(Object entity, Boolean asModified) => throw new NotImplementedException();

		public void Attach(Object entity) => throw new NotImplementedException();

		public void AttachAll(System.Collections.IEnumerable entities, Boolean asModified) => throw new NotImplementedException();

		public void AttachAll(System.Collections.IEnumerable entities) => throw new NotImplementedException();

		public DataContext Context => throw new NotSupportedException();

		public void DeleteAllOnSubmit(System.Collections.IEnumerable entities) => throw new NotImplementedException();

		public void DeleteOnSubmit(Object entity) => DeleteOnSubmit((TEntity)entity);

		public ModifiedMemberInfo[] GetModifiedMembers(Object entity) => throw new NotImplementedException();

		public Object GetOriginalEntityState(Object entity) => throw new NotImplementedException();

		public void InsertAllOnSubmit(System.Collections.IEnumerable entities) {
			foreach (Object entity in entities) {
				InsertOnSubmit(entity);
			}
		}

		public void InsertOnSubmit(Object entity) => InsertOnSubmit((TEntity)entity);

		public TEntity TryGetAttached(Object[] keyValues) => throw new NotImplementedException();


		#endregion
	}

	public class CaseInsensitiveQueries : ExpressionVisitor, IQueryProvider {
		public static Boolean Equal(String a, String b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

		public static Boolean NotEqual(String a, String b) => !a.Equals(b, StringComparison.OrdinalIgnoreCase);

		public static Boolean Contains(String a, String b) => a.IndexOf(b, StringComparison.OrdinalIgnoreCase) != -1;

		private readonly IQueryProvider _provider;
		public CaseInsensitiveQueries(IQueryProvider provider) => _provider = provider;

		protected override Expression VisitBinary(BinaryExpression node) {
			if (node.Method != null && node.Method.DeclaringType == typeof(String)) {
				if (node.NodeType == ExpressionType.Equal) {
					var method = this.GetType().GetMethod("Equal");
					return Expression.MakeBinary(ExpressionType.Equal, node.Left, node.Right, node.IsLiftedToNull, method);
				} else if (node.NodeType == ExpressionType.NotEqual) {
					var method = this.GetType().GetMethod("NotEqual");
					return Expression.MakeBinary(ExpressionType.Equal, node.Left, node.Right, node.IsLiftedToNull, method);
				}
			}

			return base.VisitBinary(node);
		}

		protected override Expression VisitMethodCall(MethodCallExpression node) {
			if (node.Method.DeclaringType == typeof(String) && node.Method.Name == "Contains") {
				var method = this.GetType().GetMethod("Contains");
				return Expression.Call(method, node.Object, node.Arguments[0]);
			}
			return base.VisitMethodCall(node);
		}

		public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => _provider.CreateQuery<TElement>(Visit(expression));

		public IQueryable CreateQuery(Expression expression) => _provider.CreateQuery(Visit(expression));

		public TResult Execute<TResult>(Expression expression) => _provider.Execute<TResult>(Visit(expression));

		public Object Execute(Expression expression) => _provider.Execute(Visit(expression));
	}

	public static class ITableExtensions {
		public static void InsertAllOnSubmit<TEntity>(this ITable<TEntity> table, IEnumerable<TEntity> entities) where TEntity : class {
			if (table is Table<TEntity> realTable) {
				realTable.InsertAllOnSubmit(entities);
				return;
			}

			foreach (TEntity entity in entities) {
				table.InsertOnSubmit(entity);
			}
		}

		public static void DeleteAllOnSubmit<TEntity>(this ITable<TEntity> table, IEnumerable<TEntity> entities) where TEntity : class {
			if (table is Table<TEntity> realTable) {
				realTable.DeleteAllOnSubmit(entities);
				return;
			}

			foreach (TEntity entity in entities) {
				table.DeleteOnSubmit(entity);
			}
		}
	}

}