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
	sealed class OptimizedStaticResourceHandler : IHandler, IDeferHandler {
		public BamlRecordType Type => BamlRecordType.OptimizedStaticResource;

		public BamlElement Translate(XamlContext ctx, BamlNode node, BamlElement parent) {
			var record = (OptimizedStaticResourceRecord)((BamlRecordNode)node).Record;
			var key = XamlResourceKey.FindKeyInSiblings(node);

			key.StaticResources.Add(node);
			return null;
		}

		public BamlElement TranslateDefer(XamlContext ctx, BamlNode node, BamlElement parent) {
			var record = (OptimizedStaticResourceRecord)((BamlRecordNode)node).Record;
			var bamlElem = new BamlElement(node);
			object key;
			if (record.IsValueTypeExtension) {
				var value = ctx.ResolveType(record.ValueId);
				string typeName = value.ToMarkupExtensionName(ctx, parent.Xaml);

				var typeElem = new XElement(ctx.GetKnownNamespace("TypeExtension", XamlContext.KnownNamespace_Xaml, parent.Xaml));
				typeElem.AddAnnotation(ctx.ResolveType(0xfd4d)); // Known type - TypeExtension
				typeElem.Add(new XElement(ctx.GetPseudoName("Ctor"), new XText(typeName).WithAnnotation(IsMemberNameAnnotation.Instance)));
				key = typeElem;
			}
			else if (record.IsValueStaticExtension) {
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

				var staticElem = new XElement(ctx.GetKnownNamespace("StaticExtension", XamlContext.KnownNamespace_Xaml, parent.Xaml));
				staticElem.AddAnnotation(ctx.ResolveType(0xfda6)); // Known type - StaticExtension
				staticElem.Add(new XElement(ctx.GetPseudoName("Ctor"), new XText(attrName).WithAnnotation(IsMemberNameAnnotation.Instance)));
				key = staticElem;
			}
			else
				key = ctx.ResolveString(record.ValueId);

			var extType = ctx.ResolveType(0xfda5);
			var resElem = new XElement(extType.ToXName(ctx));
			resElem.AddAnnotation(extType); // Known type - StaticResourceExtension
			bamlElem.Xaml = resElem;
			parent.Xaml.Element.Add(resElem);

			var attrElem = new XElement(ctx.GetPseudoName("Ctor"));
			attrElem.Add(key);
			resElem.Add(attrElem);

			return bamlElem;
		}
	}
}
