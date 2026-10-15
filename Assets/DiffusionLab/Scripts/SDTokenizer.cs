using System.Text;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

// CLIP byte BPE. Model syntax is plain English; UI languages do not change tokenization.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SDTokenizer : UdonSharpBehaviour {
    public TextAsset vocabulary,merges,byteEncoder;
    public Texture2D tokenTexture;
    public int contentTokens;
    public bool truncated,ready;
    public int[] tokenIds;
    public string input="";
    public void EncodeInput(){Encode(input);}
    private DataDictionary vocab,ranks;
    public bool Initialize(){
        if(ready&&vocab!=null&&vocab.Count==49408&&ranks!=null&&ranks.Count>40000)return true;DataToken value;
        if(!VRCJson.TryDeserializeFromJson(vocabulary.text,out value))return false;vocab=value.DataDictionary;
        if(!VRCJson.TryDeserializeFromJson(merges.text,out value))return false;ranks=value.DataDictionary;
        tokenIds=new int[77];ready=true;return true;
    }
    private int Kind(char c){if(char.IsWhiteSpace(c))return 0;if(char.IsLetter(c))return 1;if(char.IsNumber(c))return 2;return 3;}
    // Match the pinned Transformers tokenizer's BasicTokenizer Chinese splitting.
    private bool Chinese(char c){return c>=0x4e00&&c<=0x9fff||c>=0x3400&&c<=0x4dbf||c>=0xf900&&c<=0xfaff;}
    private void Word(string word){
        byte[] bytes=Encoding.UTF8.GetBytes(word);string map=byteEncoder.text;
        string[] pieces=new string[bytes.Length];int count=pieces.Length;
        if(count==0)return;
        for(int i=0;i<count;i++)pieces[i]=map[bytes[i]].ToString();pieces[count-1]+="</w>";
        while(count>1){
            int best=-1,bestRank=int.MaxValue;DataToken rank;
            for(int i=0;i<count-1;i++)if(ranks.TryGetValue(pieces[i]+" "+pieces[i+1],out rank)){
                int r=(int)rank.Double;if(r<bestRank){best=i;bestRank=r;}
            }
            if(best<0)break;
            string a=pieces[best],b=pieces[best+1];int write=0;
            for(int read=0;read<count;read++){
                if(read<count-1&&pieces[read]==a&&pieces[read+1]==b){pieces[write++]=a+b;read++;}
                else pieces[write++]=pieces[read];
            }
            count=write;
        }
        for(int i=0;i<count;i++){
            DataToken id;int n=vocab.TryGetValue(pieces[i],out id)?(int)id.Double:49407;
            if(contentTokens<75)tokenIds[contentTokens+1]=n;contentTokens++;
        }
    }
    public bool Encode(string text){
        if(!Initialize())return false;
        for(int i=0;i<77;i++)tokenIds[i]=49407;tokenIds[0]=49406;contentTokens=0;
        text=(text??"").ToLowerInvariant().Replace("&amp;","&").Replace("&quot;","\"").Replace("&#39;","'").Replace("&lt;","<").Replace("&gt;",">");
        int at=0;
        while(at<text.Length){
            char c=text[at];int kind=Kind(c);if(kind==0){at++;continue;}
            if(Chinese(c)){Word(c.ToString());at++;continue;}
            if(c=='\''){
                string tail=text.Substring(at);string contraction="";
                if(tail.StartsWith("'re")||tail.StartsWith("'ve")||tail.StartsWith("'ll"))contraction=tail.Substring(0,3);
                else if(tail.StartsWith("'s")||tail.StartsWith("'t")||tail.StartsWith("'m")||tail.StartsWith("'d"))contraction=tail.Substring(0,2);
                if(contraction.Length>0){Word(contraction);at+=contraction.Length;continue;}
            }
            int end=at+1;
            if(kind!=2)while(end<text.Length&&Kind(text[end])==kind&&!Chinese(text[end]))end++;
            Word(text.Substring(at,end-at));at=end;
        }
        truncated=contentTokens>75;
        Color[] colors=new Color[128];for(int i=0;i<77;i++)colors[i]=new Color(tokenIds[i],0,0,0);
        tokenTexture.SetPixels(colors);tokenTexture.Apply(false,false);return true;
    }
}
