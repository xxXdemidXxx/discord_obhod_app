using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
namespace ZapretDesktop {
    public static class DomainLists {
        static readonly string[] files={"list-general-user.txt","list-exclude-user.txt","ipset-exclude-user.txt"};
        public static string FilePath(string engine,int kind){if(kind<0 || kind>=files.Length)throw new InvalidDataException("Неизвестный список.");return Path.Combine(engine,"lists",files[kind]);}
        public static string Read(string engine,int kind){string path=FilePath(engine,kind);if(!File.Exists(path))return "";string text=File.ReadAllText(path);return String.Join(Environment.NewLine,text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(x=>x.Trim()!="domain.example.abc" && x.Trim()!="203.0.113.113/32" && !String.Equals(x.Trim(),"# Never leave this file empty",StringComparison.OrdinalIgnoreCase)));}
        public static string FromInput(string value,int kind){
            value=value.Trim();if(value.Length==0)throw new InvalidDataException(kind==2?"Введите IP-адрес.":"Введите адрес сайта.");
            Uri url;if(kind<2 && value.Contains("://")){if(!Uri.TryCreate(value,UriKind.Absolute,out url) || (url.Scheme!="https" && url.Scheme!="http"))throw new InvalidDataException("Введите адрес сайта или ссылку с https://.");value=url.IdnHost;}
            string normalized=Normalize(value,kind).Trim();if(normalized.Length==0 || normalized.Contains("\n") || normalized.StartsWith("#"))throw new InvalidDataException("Добавьте один адрес.");return normalized;
        }
        public static string Normalize(string text,int kind){if(kind<0 || kind>2)throw new InvalidDataException("Неизвестный список.");if(text.Length>1024*1024)throw new InvalidDataException("Список слишком большой.");var result=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var lines=text.Replace("\r","").Split('\n');if(lines.Length>5000)throw new InvalidDataException("Максимум 5000 строк в одном списке.");
            for(int i=0;i<lines.Length;i++){string line=lines[i].Trim();if(line.Length==0)continue;if(line.StartsWith("#")){result.Add(line);continue;}string normalized=line;
                if(kind<2){if(line.Contains("://") || line.IndexOfAny(new[]{'/','\\',' ',':','*','?'})>=0)throw new InvalidDataException("Строка "+(i+1)+": укажите только домен, например example.com.");try{normalized=new IdnMapping().GetAscii(line.TrimEnd('.')).ToLowerInvariant();}catch(ArgumentException){throw new InvalidDataException("Строка "+(i+1)+": неверный домен.");}IPAddress address;if(normalized.Length>253 || !normalized.Contains('.') || IPAddress.TryParse(normalized,out address) || normalized.Split('.').Any(x=>!Regex.IsMatch(x,@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")))throw new InvalidDataException("Строка "+(i+1)+": неверный домен.");}
                else{var parts=line.Split('/');IPAddress address;int mask;if(parts.Length>2 || !IPAddress.TryParse(parts[0],out address) || (address.AddressFamily==AddressFamily.InterNetwork && parts[0].Split('.').Length!=4) || (parts.Length==2 && (!Int32.TryParse(parts[1],out mask) || mask<0 || mask>(address.AddressFamily==AddressFamily.InterNetwork?32:128))))throw new InvalidDataException("Строка "+(i+1)+": укажите IP-адрес или сеть, например 192.168.1.0/24.");normalized=address.ToString()+(parts.Length==2?"/"+Int32.Parse(parts[1]):"");}
                if(seen.Add(normalized))result.Add(normalized);
            }return String.Join("\r\n",result)+(result.Count>0?"\r\n":"");
        }
        public static void Write(string engine,int kind,string text){string normalized=Normalize(text,kind),path=FilePath(engine,kind),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";if(!normalized.Split('\n').Any(x=>x.Trim().Length>0 && !x.Trim().StartsWith("#")))normalized+=(kind==2?"203.0.113.113/32":"domain.example.abc")+"\r\n";Directory.CreateDirectory(Path.GetDirectoryName(path));try{File.WriteAllText(temp,normalized,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
    }
}
