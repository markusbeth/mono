
//
// System.Reflection.Emit/ModuleBuilder.cs
//
// Author:
//   Paolo Molaro (lupus@ximian.com)
//
// (C) 2001 Ximian, Inc.  http://www.ximian.com
//

using System;
using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics.SymbolStore;
using System.IO;
using Mono.CSharp.Debugger;

namespace System.Reflection.Emit {
	public class ModuleBuilder : Module {
		private IntPtr dynamic_image;
		private TypeBuilder[] types;
		private CustomAttributeBuilder[] cattrs;
		private byte[] guid;
		private int table_idx;
		internal AssemblyBuilder assemblyb;
		private MethodBuilder[] global_methods;
		private FieldBuilder[] global_fields;
		bool is_main;
		private TypeBuilder global_type;
		private Type global_type_created;
		internal IMonoSymbolWriter symbol_writer;
		Hashtable name_cache;
		Hashtable us_string_cache = new Hashtable ();
		private int[] table_indexes;
		bool transient;

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern void basic_init (ModuleBuilder ab);

		internal ModuleBuilder (AssemblyBuilder assb, string name, string fullyqname, bool emitSymbolInfo, bool transient) {
			this.name = this.scopename = name;
			this.fqname = fullyqname;
			this.assembly = this.assemblyb = assb;
			this.transient = transient;
			guid = Guid.NewGuid().ToByteArray ();
			table_idx = get_next_table_index (this, 0x00, true);
			name_cache = new Hashtable ();

			if (emitSymbolInfo)
				GetSymbolWriter (fullyqname);
			basic_init (this);
		}

		internal void GetSymbolWriter (string filename)
		{
			Assembly assembly;
			try {
				assembly = Assembly.Load ("Mono.CSharp.Debugger");
			} catch (FileNotFoundException) {
				return;
			}

			Type type = assembly.GetType ("Mono.CSharp.Debugger.MonoSymbolWriter");
			if (type == null)
				return;

			// First get the constructor.
			{
				Type[] arg_types = new Type [1];
				arg_types [0] = typeof (ModuleBuilder);
				ConstructorInfo constructor = type.GetConstructor (arg_types);

				object[] args = new object [1];
				args [0] = this;

				if (constructor == null)
					return;

				Object instance = constructor.Invoke (args);
				if (instance == null)
					return;

				if (!(instance is IMonoSymbolWriter))
					return;

				symbol_writer = (IMonoSymbolWriter) instance;
			}
		}

		public override string FullyQualifiedName {get { return fqname;}}

		public bool IsTransient () {
			return transient;
		}

		public void CreateGlobalFunctions () 
		{
			if (global_type_created != null)
				throw new InvalidOperationException ("global methods already created");
			if (global_type != null)
				global_type_created = global_type.CreateType ();
		}

		public FieldBuilder DefineInitializedData( string name, byte[] data, FieldAttributes attributes) {
			if (data == null)
				throw new ArgumentNullException ("data");

			FieldBuilder fb = DefineUninitializedData (name, data.Length, 
													   attributes | FieldAttributes.HasFieldRVA);
			fb.SetRVAData (data);

			return fb;
		}

		public FieldBuilder DefineUninitializedData( string name, int size, FieldAttributes attributes) {
			if (name == null)
				throw new ArgumentNullException ("name");
			if (global_type_created != null)
				throw new InvalidOperationException ("global fields already created");
			if (global_type == null)
				global_type = new TypeBuilder (this, 0);

			string typeName = "$ArrayType$" + size;
			Type datablobtype = GetType (typeName, false, false);
			if (datablobtype == null) {
				TypeBuilder tb = DefineType (typeName, 
				    TypeAttributes.Public|TypeAttributes.ExplicitLayout|TypeAttributes.Sealed,
					assemblyb.corlib_value_type, null, PackingSize.Size1, size);
				tb.CreateType ();
				datablobtype = tb;
			}
			FieldBuilder fb = global_type.DefineField (name, datablobtype, attributes|FieldAttributes.Static);

			if (global_fields != null) {
				FieldBuilder[] new_fields = new FieldBuilder [global_fields.Length+1];
				System.Array.Copy (global_fields, new_fields, global_fields.Length);
				new_fields [global_fields.Length] = fb;
				global_fields = new_fields;
			} else {
				global_fields = new FieldBuilder [1];
				global_fields [0] = fb;
			}
			return fb;
		}

		public MethodBuilder DefineGlobalMethod (string name, MethodAttributes attributes, Type returnType, Type[] parameterTypes)
		{
			return DefineGlobalMethod (name, attributes, CallingConventions.Standard, returnType, parameterTypes);
		}
		
		public MethodBuilder DefineGlobalMethod (string name, MethodAttributes attributes, CallingConventions callingConvention, Type returnType, Type[] parameterTypes)
		{
			if (name == null)
				throw new ArgumentNullException ("name");
			if ((attributes & MethodAttributes.Static) == 0)
				throw new ArgumentException ("global methods must be static");
			if (global_type_created != null)
				throw new InvalidOperationException ("global methods already created");
			if (global_type == null)
				global_type = new TypeBuilder (this, 0);
			MethodBuilder mb = global_type.DefineMethod (name, attributes, callingConvention, returnType, parameterTypes);

			if (global_methods != null) {
				MethodBuilder[] new_methods = new MethodBuilder [global_methods.Length+1];
				System.Array.Copy (global_methods, new_methods, global_methods.Length);
				new_methods [global_methods.Length] = mb;
				global_methods = new_methods;
			} else {
				global_methods = new MethodBuilder [1];
				global_methods [0] = mb;
			}
			return mb;
		}
		
		[MonoTODO]
		public TypeBuilder DefineType (string name) {
			// FIXME: LAMESPEC: what other attributes should we use here as default?
			return DefineType (name, TypeAttributes.Public, typeof(object), null);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr) {
			return DefineType (name, attr, typeof(object), null);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr, Type parent) {
			return DefineType (name, attr, parent, null);
		}

		private TypeBuilder DefineType (string name, TypeAttributes attr, Type parent, Type[] interfaces, PackingSize packsize, int typesize) {
			TypeBuilder res = new TypeBuilder (this, name, attr, parent, interfaces, packsize, typesize, null);
			if (types != null) {
				TypeBuilder[] new_types = new TypeBuilder [types.Length + 1];
				System.Array.Copy (types, new_types, types.Length);
				new_types [types.Length] = res;
				types = new_types;
			} else {
				types = new TypeBuilder [1];
				types [0] = res;
			}
			name_cache.Add (name, res);
			return res;
		}

		internal void RegisterTypeName (TypeBuilder tb, string name) {
			name_cache.Add (name, tb);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr, Type parent, Type[] interfaces) {
			return DefineType (name, attr, parent, interfaces, PackingSize.Unspecified, TypeBuilder.UnspecifiedTypeSize);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr, Type parent, int typesize) {
			return DefineType (name, attr, parent, null, PackingSize.Unspecified, TypeBuilder.UnspecifiedTypeSize);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr, Type parent, PackingSize packsize) {
			return DefineType (name, attr, parent, null, packsize, TypeBuilder.UnspecifiedTypeSize);
		}

		public TypeBuilder DefineType (string name, TypeAttributes attr, Type parent, PackingSize packsize, int typesize) {
			return DefineType (name, attr, parent, null, packsize, typesize);
		}

		public MethodInfo GetArrayMethod( Type arrayClass, string methodName, CallingConventions callingConvention, Type returnType, Type[] parameterTypes) {
			return new MonoArrayMethod (arrayClass, methodName, callingConvention, returnType, parameterTypes);
		}

		public EnumBuilder DefineEnum( string name, TypeAttributes visibility, Type underlyingType) {
			EnumBuilder eb = new EnumBuilder (this, name, visibility, underlyingType);
			return eb;
		}

		public override Type GetType( string className) {
			return GetType (className, false, false);
		}
		
		public override Type GetType( string className, bool ignoreCase) {
			return GetType (className, false, ignoreCase);
		}

		private TypeBuilder search_in_array (TypeBuilder[] arr, string className) {
			int i;
			for (i = 0; i < arr.Length; ++i) {
				if (String.Compare (className, arr [i].FullName, true) == 0) {
					return arr [i];
				}
			}
			return null;
		}

		private TypeBuilder search_nested_in_array (TypeBuilder[] arr, string className) {
			int i;
			for (i = 0; i < arr.Length; ++i) {
				if (String.Compare (className, arr [i].Name, true) == 0)
					return arr [i];
			}
			return null;
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern Type create_modified_type (TypeBuilder tb, string modifiers);

		static char[] type_modifiers = {'&', '[', '*'};

		private TypeBuilder GetMaybeNested (TypeBuilder t, string className) {
			int subt;
			string pname, rname;

			subt = className.IndexOf ('+');
			if (subt < 0) {
				if (t.subtypes != null)
					return search_nested_in_array (t.subtypes, className);
				return null;
			}
			if (t.subtypes != null) {
				pname = className.Substring (0, subt);
				rname = className.Substring (subt + 1);
				TypeBuilder result = search_nested_in_array (t.subtypes, pname);
				if (result != null)
					return GetMaybeNested (result, rname);
			}
			return null;
		}
		
		public override Type GetType (string className, bool throwOnError, bool ignoreCase) {
			int subt;
			string orig = className;
			string modifiers;
			TypeBuilder result = null;

			if (types == null && throwOnError)
				throw new TypeLoadException (className);

			subt = className.IndexOfAny (type_modifiers);
			if (subt >= 0) {
				modifiers = className.Substring (subt);
				className = className.Substring (0, subt);
			} else
				modifiers = null;

			if (!ignoreCase) {
				result =  name_cache [className] as TypeBuilder;
			} else {
				subt = className.IndexOf ('+');
				if (subt < 0) {
					if (types != null)
						result = search_in_array (types, className);
				} else {
					string pname, rname;
					pname = className.Substring (0, subt);
					rname = className.Substring (subt + 1);
					result = search_in_array (types, pname);
					if (result != null)
						result = GetMaybeNested (result, rname);
				}
			}
			if ((result == null) && throwOnError)
				throw new TypeLoadException (orig);
			if (result != null && (modifiers != null))
				return create_modified_type (result, modifiers);
			return result;
		}

		internal int get_next_table_index (object obj, int table, bool inc) {
			if (table_indexes == null) {
				table_indexes = new int [64];
				for (int i=0; i < 64; ++i)
					table_indexes [i] = 1;
				/* allow room for .<Module> in TypeDef table */
				table_indexes [0x02] = 2;
			}
			// Console.WriteLine ("getindex for table "+table.ToString()+" got "+table_indexes [table].ToString());
			if (inc)
				return table_indexes [table]++;
			return table_indexes [table];
		}

		public void SetCustomAttribute( CustomAttributeBuilder customBuilder) {
			if (cattrs != null) {
				CustomAttributeBuilder[] new_array = new CustomAttributeBuilder [cattrs.Length + 1];
				cattrs.CopyTo (new_array, 0);
				new_array [cattrs.Length] = customBuilder;
				cattrs = new_array;
			} else {
				cattrs = new CustomAttributeBuilder [1];
				cattrs [0] = customBuilder;
			}
		}
		public void SetCustomAttribute( ConstructorInfo con, byte[] binaryAttribute) {
			SetCustomAttribute (new CustomAttributeBuilder (con, binaryAttribute));
		}

		public ISymbolWriter GetSymWriter () {
			return symbol_writer;
		}

		public ISymbolDocumentWriter DefineDocument (string url, Guid language, Guid languageVendor, Guid documentType) {
			if (symbol_writer == null)
				throw new InvalidOperationException ();

			return symbol_writer.DefineDocument (url, language, languageVendor, documentType);
		}

		public override Type [] GetTypes ()
		{
			if (types == null)
				return new TypeBuilder [0];

			int n = types.Length;
			TypeBuilder [] copy = new TypeBuilder [n];
			Array.Copy (types, copy, n);

			return copy;
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern int getUSIndex (ModuleBuilder mb, string str);

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern int getToken (ModuleBuilder mb, object obj);

		internal int GetToken (string str) {
			if (us_string_cache.Contains (str))
				return (int)us_string_cache [str];
			int result = getUSIndex (this, str);
			us_string_cache [str] = result;
			return result;
		}

		internal int GetToken (MemberInfo member) {
			return getToken (this, member);
		}

		internal int GetToken (SignatureHelper helper) {
			return getToken (this, helper);
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern void build_metadata (ModuleBuilder mb);

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private static extern int getDataChunk (ModuleBuilder mb, byte[] buf, int offset);

		internal void Save ()
		{
			if (transient)
				return;

			build_metadata (this);

			if (symbol_writer != null) {
				string res_name;
				if (is_main)
					res_name = "MonoSymbolFile";
				else
					res_name = "MonoSymbolFile:" + fqname;
				byte[] data = symbol_writer.CreateSymbolFile (assemblyb);
				assemblyb.EmbedResource (res_name, data, ResourceAttributes.Public);
			}

			string fileName = fqname;
			if (assemblyb.AssemblyDir != null)
				fileName = System.IO.Path.Combine (assemblyb.AssemblyDir, fileName);

			byte[] buf = new byte [65536];
			FileStream file;
			int count, offset;

			file = new FileStream (fileName, FileMode.Create, FileAccess.Write);

			offset = 0;
			while ((count = getDataChunk (this, buf, offset)) != 0) {
				file.Write (buf, 0, count);
				offset += count;
			}
			file.Close ();

			//
			// The constant 0x80000000 is internal to Mono, it means `make executable'
			//
			File.SetAttributes (fileName, (FileAttributes) (unchecked ((int) 0x80000000)));
		}

		internal string FileName {
			get {
				return fqname;
			}
		}

		internal bool IsMain {
			set {
				is_main = value;
			}
		}
	}
}
