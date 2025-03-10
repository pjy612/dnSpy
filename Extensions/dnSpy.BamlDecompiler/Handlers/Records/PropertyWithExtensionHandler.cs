/*
	Copyright (c) 2015 Ki

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using System.Xml.Linq;
using dnSpy.BamlDecompiler.Baml;
using dnSpy.BamlDecompiler.Xaml;

namespace dnSpy.BamlDecompiler.Handlers {
	sealed class PropertyWithExtensionHandler : IHandler {
		public BamlRecordType Type => BamlRecordType.PropertyWithExtension;

		public BamlElement Translate(XamlContext ctx, BamlNode node, BamlElement parent) {
			var record = (PropertyWithExtensionRecord)((BamlRecordNode)node).Record;
			var extTypeId = record.ExtensionTypeId;
			bool valTypeExt = record.IsValueTypeExtension;
			bool valStaticExt = record.IsValueStaticExtension;

			var elemType = parent.Xaml.Element.Annotation<XamlType>();
			var xamlProp = ctx.ResolveProperty(record.AttributeId);
			var extType = ctx.ResolveType(unchecked((ushort)-extTypeId));
			extType.ResolveNamespace(parent.Xaml, ctx);

			var ext = new XamlExtension(extType);
			if (valTypeExt || extTypeId == (short)KnownTypes.TypeExtension) {
				var value = ctx.ResolveType(record.ValueId);

				object[] initializer = { new XamlMemberName(value.ToMarkupExtensionName(ctx, parent.Xaml)) };
				if (valTypeExt)
					initializer = new object[] { new XamlExtension(ctx.ResolveType(0xfd4d)) { Initializer = initializer } }; // Known type - TypeExtension

				ext.Initializer = initializer;
			}
			else if (extTypeId == (short)KnownTypes.TemplateBindingExtension) {
				var value = ctx.ResolveProperty(record.ValueId);

				value.DeclaringType.ResolveNamespace(parent.Xaml, ctx);
				ext.Initializer = new object[] { new XamlMemberName(value.ToMarkupExtensionName(ctx, parent.Xaml)) };
			}
			else if (valStaticExt || extTypeId == (short)KnownTypes.StaticExtension) {
				string attrName;
				if (record.ValueId > 0x7fff) {
					var resId = BamlUtils.GetKnownResourceIdFromBamlId(record.ValueId, out bool isKey);
					var res = ctx.Baml.KnownThings.Resources(resId);
					string name;
					if (isKey)
						name = res.TypeName + "." + res.KeyName;
					else
						name = res.TypeName + "." + res.PropertyName;
					var xmlns = ctx.GetXmlNamespace(XamlContext.KnownNamespace_Presentation);
					attrName = ctx.ToString(parent.Xaml, xmlns.GetName(name));
				}
				else {
					var value = ctx.ResolveProperty(record.ValueId);

					value.DeclaringType.ResolveNamespace(parent.Xaml, ctx);
					attrName = value.ToMarkupExtensionName(ctx, parent.Xaml);
				}

				object[] initializer = { new XamlMemberName(attrName) };
				if (valStaticExt)
					initializer = new object[] { new XamlExtension(ctx.ResolveType(0xfda6)) { Initializer = initializer } }; // Known type - StaticExtension

				ext.Initializer = initializer;
			}
			else {
				ext.Initializer = new object[] { XamlUtils.Escape(ctx.ResolveString(record.ValueId)) };
			}

			var extValue = ext.ToString(ctx, parent.Xaml);
			var attr = new XAttribute(xamlProp.ToXName(ctx, parent.Xaml, xamlProp.IsAttachedTo(elemType)), extValue);
			parent.Xaml.Element.Add(attr);

			return null;
		}
	}
}
