/* part of Pyrolite, by Irmen de Jong (irmen@razorvine.net) */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable MemberInitializerValueIgnored
// ReSharper disable InconsistentNaming
// ReSharper disable LoopCanBeConvertedToQuery
// ReSharper disable InvertIf
// ReSharper disable SuggestBaseTypeForParameter

namespace Razorvine.Pickle
{
	
/// <summary>
/// Pickle an object graph into a Python-compatible pickle stream. For
/// simplicity, the only supported pickle protocol at this time is protocol 2. 
/// See README.txt for a table with the type mapping.
/// This class is NOT threadsafe! (Don't use the same pickler from different threads)
/// </summary>
public class Pickler : IDisposable {

	// ReSharper disable once UnusedMember.Global
	public static int HIGHEST_PROTOCOL = 2;

	protected const int MAX_RECURSE_DEPTH = 200;
	protected Stream outs;
	protected int recurse;	// recursion level
	protected const int PROTOCOL = 2;
	protected static readonly IDictionary<Type, IObjectPickler> customPicklers = new Dictionary<Type, IObjectPickler>();
	protected readonly bool useMemo=true;
	protected IDictionary<object, int> memo;		// maps objects to memo index
	
	/**
	 * Create a Pickler.
	 */
	public Pickler() : this(true) {
	}

	/**
	 * Create a Pickler. Specify if it is to use a memo table or not.
	 * The memo table is NOT reused across different calls.
	 * If you use a memo table, you can only pickle objects that are hashable.
	 */
	public Pickler(bool useMemo) {
		this.useMemo=useMemo;
	}
	
	/**
	 * Close the pickler stream, discard any internal buffers.
	 */
	public void close() {
		memo = null;
		outs.Flush();
		outs.Close();
	}

	/**
	 * Register additional object picklers for custom classes.
	 * If you register an interface or abstract base class, it means the pickler is used for 
	 * the whole inheritance tree of all classes ultimately implementing that interface or abstract base class.
	 * If you register a normal concrete class, the pickler is only used for objects of exactly that particular class.
	 */
	public static void registerCustomPickler(Type clazz, IObjectPickler pickler) {
		customPicklers[clazz]=pickler;
	}
	
	/**
	 * Pickle a given object graph, returning the result as a byte array.
	 */
	public byte[] dumps(object o) {
		MemoryStream bo = new MemoryStream();
		dump(o, bo);
		bo.Flush();
		return bo.ToArray();
	}

	/**
	 * Pickle a given object graph, writing the result to the output stream.
	 */
	public void dump(object o, Stream stream) {
		outs = stream;
		recurse = 0;
		if(useMemo)
			memo = new Dictionary<object, int>();
		outs.WriteByte(Opcodes.PROTO);
		outs.WriteByte(PROTOCOL);
		save(o);
		memo = null;  // get rid of the memo table
		outs.WriteByte(Opcodes.STOP);
		outs.Flush();
		if(recurse!=0)  // sanity check
			throw new PickleException("recursive structure error, please report this problem");
	}

	/**
	 * Pickle a single object and write its pickle representation to the output stream.
	 * Normally this is used internally by the pickler, but you can also utilize it from
	 * within custom picklers. This is handy if as part of the custom pickler, you need
	 * to write a couple of normal objects such as strings or ints, that are already
	 * supported by the pickler.
	 * This method can be called recursively to output sub-objects.
	 */
	public void save(object o) {
		recurse++;
		if(recurse>MAX_RECURSE_DEPTH)
			throw new StackOverflowException("recursion too deep in Pickler.save (>"+MAX_RECURSE_DEPTH+")");

		// null type?
		if(o==null) {
			outs.WriteByte(Opcodes.NONE);
			recurse--;
			return;
		}


		Type t=o.GetType();
		
		// check the memo table, otherwise simply dispatch
		if(LookupMemo(t, o) || dispatch(t, o)){
			recurse--;
			return;
		}

		throw new PickleException("couldn't pickle object of type "+t);
	}
	
	/**
	 * Write the object to the memo table and output a memo write opcode
	 * Only works for hashable objects
	 */
	protected void WriteMemo(object obj) {
		if(!useMemo)
			return;
		if(!memo.ContainsKey(obj))
		{
			int memo_index = memo.Count;
			memo[obj] = memo_index;
			if(memo_index<=0xFF)
			{
				outs.WriteByte(Opcodes.BINPUT);
				outs.WriteByte((byte)memo_index);
			}
			else
			{
				outs.WriteByte(Opcodes.LONG_BINPUT);
				var index_bytes = PickleUtils.integer_to_bytes(memo_index);
				outs.Write(index_bytes, 0, 4);
			}
		}
	}
	
	/**
	 * Check the memo table and output a memo lookup if the object is found
	 */
	private bool LookupMemo(Type objectType, object obj) {
		if(!useMemo)
			return false;
		if(!objectType.IsPrimitive)
		{
			int memo_index;
			if(memo.TryGetValue(obj, out memo_index))
			{
				if(memo_index<=0xff)
				{
					outs.WriteByte(Opcodes.BINGET);
					outs.WriteByte((byte)memo_index);
				}
				else
				{
					outs.WriteByte(Opcodes.LONG_BINGET);
					var index_bytes = PickleUtils.integer_to_bytes(memo_index);
					outs.Write(index_bytes, 0, 4);
				}
				return true;
			}
		}
		return false;
	}

	/**
	 * Process a single object to be pickled.
	 */
	private bool dispatch(Type t, object o) {
		// is it a primitive array?
		if(o is Array) {
			Type componentType=t.GetElementType();
			if(componentType != null && componentType.IsPrimitive) {
				put_arrayOfPrimitives(componentType, o);
			} else {
				put_arrayOfObjects((object[])o);
			}
			return true;
		}
		
		// first the primitive types
		if(o is bool) {
			put_bool((bool)o);
			return true;
		}
		if(o is byte) {
			put_long((byte)o);
			return true;
		}
		if(o is sbyte) {
			put_long((sbyte)o);
			return true;
		}
		if(o is short) {
			put_long((short)o);
			return true;
		}
		if(o is ushort) {
			put_long((ushort)o);
			return true;
		}
		if(o is int) {
			put_long((int)o);
			return true;
		}
		if(o is uint) {
			put_long((uint)o);
			return true;
		}
		if(o is long) {
			put_long((long)o);
			return true;
		}
		if(o is ulong) {
			put_ulong((ulong)o);
			return true;
		}
		if(o is float) {
			put_float((float)o);
			return true;
		}
		if(o is double) {
			put_float((double)o);
			return true;
		}
		if(o is char) {
			put_string(""+o);
			return true;
		}
		
		// check registry
		IObjectPickler custompickler = getCustomPickler(t);
		if(custompickler!=null) {
			custompickler.pickle(o, outs, this);
			WriteMemo(o);
			return true;
		}
		
		// more complex types
		var s = o as string;
		if(s != null) {
			put_string(s);
			return true;
		}
		if(o is decimal) {
			put_decimal((decimal)o);
			return true;
		}
		if(o is DateTime) {
			put_datetime((DateTime)o);
			return true;
		}
		if(o is TimeSpan) {
			put_timespan((TimeSpan)o);
			return true;
		}
		if(t.IsGenericType && t.GetGenericTypeDefinition()==typeof(HashSet<>)) {
			put_set((IEnumerable)o);
			return true;
		}

		var dictionary = o as IDictionary;
		if(dictionary != null) {
			put_map(dictionary);
			return true;
		}

		var list = o as IList;
		if(list != null) {
			put_enumerable(list);
			return true;
		}

		var enumerable = o as IEnumerable;
		if(enumerable != null) {
			put_enumerable(enumerable);
			return true;
		}
		if(o is Enum) {
			put_string(o.ToString());
			return true;
		}
		
		DataContractAttribute dca = (DataContractAttribute) Attribute.GetCustomAttribute(t, typeof(DataContractAttribute));
		if(dca!=null) {
			put_datacontract(t, o, dca);
			return true;
		}
		
		SerializableAttribute sa = (SerializableAttribute) Attribute.GetCustomAttribute(t, typeof(SerializableAttribute));
		if(sa!=null) {
			put_serializable(t, o);
			return true;
		}
		
		if(hasPublicProperties(o)) {
			put_objwithproperties(o);
			return true;
		}

		return false;
	}
	
	[SuppressMessage("ReSharper", "MemberCanBeMadeStatic.Global")]
	protected IObjectPickler getCustomPickler(Type t)
	{
		IObjectPickler pickler;
		if(customPicklers.TryGetValue(t, out pickler))
			return pickler;		// exact match
		
		// check if there's a custom pickler registered for an interface or abstract base class
		// that this object implements or inherits from.
		foreach(var x in customPicklers) {
			if(x.Key.IsAssignableFrom(t)) {
				return x.Value;
			}
		}

		return null;
	}

	private static bool hasPublicProperties(object o)
	{
		var props=o.GetType().GetProperties();
		return props.Length>0;
	}

	private void put_datetime(DateTime dt) {
		outs.WriteByte(Opcodes.GLOBAL);
		var bytes=Encoding.Default.GetBytes("datetime\ndatetime\n");
		outs.Write(bytes,0,bytes.Length);
		outs.WriteByte(Opcodes.MARK);
		save(dt.Year);
		save(dt.Month);
		save(dt.Day);
		save(dt.Hour);
		save(dt.Minute);
		save(dt.Second);
		save(dt.Millisecond*1000);
		outs.WriteByte(Opcodes.TUPLE);
		outs.WriteByte(Opcodes.REDUCE);
		WriteMemo(dt);
	}

	private void put_timespan(TimeSpan ts) {
		outs.WriteByte(Opcodes.GLOBAL);
		var bytes=Encoding.Default.GetBytes("datetime\ntimedelta\n");
		outs.Write(bytes,0,bytes.Length);
		save(ts.Days);
		save(ts.Hours*3600+ts.Minutes*60+ts.Seconds);
		save(ts.Milliseconds*1000);
		outs.WriteByte(Opcodes.TUPLE3);
		outs.WriteByte(Opcodes.REDUCE);	
		WriteMemo(ts);
	}

	private void put_enumerable(IEnumerable list) {
		outs.WriteByte(Opcodes.EMPTY_LIST);
		WriteMemo(list);
		outs.WriteByte(Opcodes.MARK);
		foreach(var o in list) {
			save(o);
		}
		outs.WriteByte(Opcodes.APPENDS);
	}

	private void put_map(IDictionary o) {
		outs.WriteByte(Opcodes.EMPTY_DICT);
		WriteMemo(o);
		outs.WriteByte(Opcodes.MARK);
		foreach(var k in o.Keys) {
			save(k);
			save(o[k]);
		}
		outs.WriteByte(Opcodes.SETITEMS);
	}

	private void put_set(IEnumerable o) {
		outs.WriteByte(Opcodes.GLOBAL);
		var output=Encoding.ASCII.GetBytes("__builtin__\nset\n");
		outs.Write(output,0,output.Length);
		outs.WriteByte(Opcodes.EMPTY_LIST);
		outs.WriteByte(Opcodes.MARK);
		foreach(object x in o) {
			save(x);
		}
		outs.WriteByte(Opcodes.APPENDS);
		outs.WriteByte(Opcodes.TUPLE1);
		outs.WriteByte(Opcodes.REDUCE);
		WriteMemo(o);   // sets cannot contain self-references (because not hashable) so it is fine to put this at the end
	}

	private void put_arrayOfObjects(object[] array)
	{
		switch (array.Length)
		{
			// 0 objects->EMPTYTUPLE
			// 1 object->TUPLE1
			// 2 objects->TUPLE2
			// 3 objects->TUPLE3
			// 4 or more->MARK+items+TUPLE
			case 0:
				outs.WriteByte(Opcodes.EMPTY_TUPLE);
				break;
			case 1:
				if(array[0]==array)
					throw new PickleException("recursive array not supported, use list");
				save(array[0]);
				outs.WriteByte(Opcodes.TUPLE1);
				break;
			case 2:
				if(array[0]==array || array[1]==array)
					throw new PickleException("recursive array not supported, use list");
				save(array[0]);
				save(array[1]);
				outs.WriteByte(Opcodes.TUPLE2);
				break;
			case 3:
				if(array[0]==array || array[1]==array || array[2]==array)
					throw new PickleException("recursive array not supported, use list");
				save(array[0]);
				save(array[1]);
				save(array[2]);
				outs.WriteByte(Opcodes.TUPLE3);
				break;
			default:
				outs.WriteByte(Opcodes.MARK);
				foreach(object o in array) {
					if(o==array)
						throw new PickleException("recursive array not supported, use list");
					save(o);
				}
				outs.WriteByte(Opcodes.TUPLE);
				break;
		}

		WriteMemo(array);		// tuples cannot contain self-references so it is fine to put this at the end
	}

	private void put_arrayOfPrimitives(Type t, object array) {
			
		byte[] output;

		if(t==typeof(bool)) {
			// a bool[] isn't written as an array but rather as a tuple
			var source=(bool[])array;
			// this is stupid, but seems to be necessary because you can't cast a bool[] to an object[]
			var boolarray=new object[source.Length];
			Array.Copy(source, boolarray, source.Length);
			put_arrayOfObjects(boolarray);
			return;
		}
		if(t==typeof(char)) {
			// a char[] isn't written as an array but rather as a unicode string
			string s=new string((char[])array);
			put_string(s);
			return;
		}		
		if(t==typeof(byte)) {
			// a byte[] isn't written as an array but rather as a bytearray object
			outs.WriteByte(Opcodes.GLOBAL);
			output=Encoding.ASCII.GetBytes("__builtin__\nbytearray\n");
			outs.Write(output,0,output.Length);
			string str=PickleUtils.rawStringFromBytes((byte[])array);
			put_string(str);
			put_string("latin-1");	// this is what python writes in the pickle
			outs.WriteByte(Opcodes.TUPLE2);
			outs.WriteByte(Opcodes.REDUCE);
			WriteMemo(array);
			return;
		} 
		
		outs.WriteByte(Opcodes.GLOBAL);
		output=Encoding.ASCII.GetBytes("array\narray\n");
		outs.Write(output,0,output.Length);
		outs.WriteByte(Opcodes.SHORT_BINSTRING);		// array typecode follows
		outs.WriteByte(1); // typecode is 1 char
		
		if(t==typeof(sbyte)) {
			outs.WriteByte((byte)'b'); // signed char
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(sbyte s in (sbyte[])array) {
				save(s);
			}
		} else if(t==typeof(short)) {
			outs.WriteByte((byte)'h'); // signed short
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(short s in (short[])array) {
				save(s);
			}
		} else if(t==typeof(ushort)) {
			outs.WriteByte((byte)'H'); // unsigned short
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(ushort s in (ushort[])array) {
				save(s);
			}
		} else if(t==typeof(int)) {
			outs.WriteByte((byte)'i'); // signed int
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(int i in (int[])array) {
				save(i);
			}
		} else if(t==typeof(uint)) {
			outs.WriteByte((byte)'I'); // unsigned int
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(uint i in (uint[])array) {
				save(i);
			}
		} else if(t==typeof(long)) {
			outs.WriteByte((byte)'l');  // signed long
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(long v in (long[])array) {
				save(v);
			}
		} else if(t==typeof(ulong)) {
			outs.WriteByte((byte)'L');  // unsigned long
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(ulong v in (ulong[])array) {
				save(v);
			}
		} else if(t==typeof(float)) {
			outs.WriteByte((byte)'f');  // float
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(float f in (float[])array) {
				save(f);
			}
		} else if(t==typeof(double)) {
			outs.WriteByte((byte)'d');  // double
			outs.WriteByte(Opcodes.EMPTY_LIST);
			outs.WriteByte(Opcodes.MARK);
			foreach(double d in (double[])array) {
				save(d);
			}
		} 
		
		outs.WriteByte(Opcodes.APPENDS);
		outs.WriteByte(Opcodes.TUPLE2);
		outs.WriteByte(Opcodes.REDUCE);

		WriteMemo(array);		// array of primitives can by definition never be recursive, so okay to put this at the end
	}

	private void put_decimal(decimal d) {
		//"cdecimal\nDecimal\nU\n12345.6789\u0085R."
		outs.WriteByte(Opcodes.GLOBAL);
		var output=Encoding.ASCII.GetBytes("decimal\nDecimal\n");
		outs.Write(output,0,output.Length);
		put_string(Convert.ToString(d, CultureInfo.InvariantCulture));
		outs.WriteByte(Opcodes.TUPLE1);
		outs.WriteByte(Opcodes.REDUCE);
		WriteMemo(d);
	}

	private void put_string(string str) {
		var encoded=Encoding.UTF8.GetBytes(str);
		outs.WriteByte(Opcodes.BINUNICODE);
		var output=PickleUtils.integer_to_bytes(encoded.Length);
		outs.Write(output,0,output.Length);
		outs.Write(encoded,0,encoded.Length);
		WriteMemo(str);
	}

	private void put_float(double d) {
		outs.WriteByte(Opcodes.BINFLOAT);
		var output=PickleUtils.double_to_bytes_bigendian(d);
		outs.Write(output,0,output.Length);
	}

	private void put_long(long v) {
		byte[] output;
		// choose optimal representation
		// first check 1 and 2-byte unsigned ints:
		if(v>=0) {
			if(v<=0xff) {
				outs.WriteByte(Opcodes.BININT1);
				outs.WriteByte((byte)v);
				return;
			}
			if(v<=0xffff) {
				outs.WriteByte(Opcodes.BININT2);
				outs.WriteByte((byte)(v&0xff));
				outs.WriteByte((byte)(v>>8));
				return;
			}
		}
		
		// 4-byte signed int?
		long high_bits=v>>31;  // shift sign extends
		if(high_bits==0 || high_bits==-1) {
			// All high bits are copies of bit 2**31, so the value fits in a 4-byte signed int.
			outs.WriteByte(Opcodes.BININT);
			output=PickleUtils.integer_to_bytes((int)v);
			outs.Write(output,0,output.Length);
			return;
		}
		
		// int too big, store it as text
		outs.WriteByte(Opcodes.INT);
		output=Encoding.ASCII.GetBytes(""+v);
		outs.Write(output, 0, output.Length);
		outs.WriteByte((byte)'\n');
	}

	private void put_ulong(ulong u) {
		if(u<=long.MaxValue) {
			long l=(long)u;
			put_long(l);
		} else {
			// ulong too big for a signed long, store it as text instead.
			outs.WriteByte(Opcodes.INT);
			var output=Encoding.ASCII.GetBytes(u.ToString());
			outs.Write(output, 0, output.Length);
			outs.WriteByte((byte)'\n');
		}
	}

	private void put_bool(bool b)
	{
		outs.WriteByte(b ? Opcodes.NEWTRUE : Opcodes.NEWFALSE);
	}

	private void put_objwithproperties(object o) {
		var properties=o.GetType().GetProperties();
		var map=new Dictionary<string, object>();
		foreach(var propinfo in properties) {
			if(propinfo.CanRead) {
				string name=propinfo.Name;
				try {
					map[name]=propinfo.GetValue(o, null);
				} catch (Exception x) {
					throw new PickleException("cannot pickle object:",x);
				}
			}
		}
		
		// if we're dealing with an anonymous type, don't output the type name.
		if(!o.GetType().Name.StartsWith("<>"))
			map["__class__"]=o.GetType().FullName;

		save(map);
	}

	private void put_serializable(Type t, object o)
	{
		var map=new Dictionary<string, object>();
		var fields = t.GetFields();
		foreach(var field in fields) {
			if(field.GetCustomAttribute(typeof(NonSerializedAttribute))==null) {
				string name=field.Name;
				try {
					map[name]=field.GetValue(o);
				} catch (Exception x) {
					throw new PickleException("cannot pickle [Serializable] object:",x);
				}
			}
		}
		var properties=t.GetProperties();
		foreach(var propinfo in properties) {
			if(propinfo.CanRead) {
				string name=propinfo.Name;
				try {
					map[name]=propinfo.GetValue(o, null);
				} catch (Exception x) {
					throw new PickleException("cannot pickle [Serializable] object:",x);
				}
			}
		}

		// if we're dealing with an anonymous type, don't output the type name.
		if(!o.GetType().Name.StartsWith("<>"))
			map["__class__"]=o.GetType().FullName;

		save(map);
	}

	private void put_datacontract(Type t, object o, DataContractAttribute dca)
	{
		var fields = t.GetFields();
		var map=new Dictionary<string, object>();
		foreach(var field in fields) {
			DataMemberAttribute dma = (DataMemberAttribute) field.GetCustomAttribute(typeof(DataMemberAttribute));
			if(dma!=null) {
				string name=dma.Name;
				try {
					map[name]=field.GetValue(o);
				} catch (Exception x) {
					throw new PickleException("cannot pickle [DataContract] object:",x);
				}
			}
		}
		var properties=t.GetProperties();
		foreach(var propinfo in properties) {
			if(propinfo.CanRead && propinfo.GetCustomAttribute(typeof(DataMemberAttribute))!=null) {
				string name=propinfo.Name;
				try {
					map[name]=propinfo.GetValue(o, null);
				} catch (Exception x) {
					throw new PickleException("cannot pickle [DataContract] object:",x);
				}
			}
		}

		if(string.IsNullOrEmpty(dca.Name)) {
			// if we're dealing with an anonymous type, don't output the type name.
			if(!o.GetType().Name.StartsWith("<>"))
				map["__class__"]=o.GetType().FullName;
		} else {
			map["__class__"] = dca.Name;
		}

		save(map);
	}
		
	public void Dispose()
	{
		close();
	}
}

}
