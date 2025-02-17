(* File MicroC/Interp.c
   Interpreter for micro-C, a fraction of the C language 
   sestoft@itu.dk * 2010-01-07, 2014-10-18

   A value is an integer; it may represent an integer or a pointer,
   where a pointer is just an address in the store (of a variable or
   pointer or the base address of an array).  The environment maps a
   variable to an address (location), and the store maps a location to
   an integer.  This freely permits pointer arithmetics, as in real C.
   Expressions can have side effects.  A function takes a list of
   typed arguments and may optionally return a result.

   For now, arrays can be one-dimensional only.  For simplicity, we
   represent an array as a variable which holds the address of the
   first array element.  This is consistent with the way array-type
   parameters are handled in C (and the way that array-type variables
   were handled in the B language), but not with the way array-type
   variables are handled in C.

   The store behaves as a stack, so all data are stack allocated:
   variables, function parameters and arrays.  

   The return statement is not implemented (for simplicity), so all
   functions should have return type void.  But there is as yet no
   typecheck, so be careful.
 *)

module Interp

open Absyn
open Debug
(* ------------------------------------------------------------------- *)


(* Simple environment operations *)
// 多态类型 env 
// 环境env 是 元组 ("name",data)的列表 
// 值 data可以是任意类型

type mem =
  | INT of Int
  | STRING of string
  | POINTER of int
  | FLOAT of float
  | CHAR of char
  | BOOLEAN of bool

type 'data env = (string * 'data) list

//环境查找函数 
//在环境env上查找名称为x的值
let rec lookup env x = 
    match env with 
    | []         -> failwith (x + " not found")
    | (y, v)::yr -> if x=y then v else lookup yr x

let rec structLookup env x index=
    match env with
    | []                            -> failwith(x + " not found")
    | (name, arglist, size)::rhs    -> if x = name then (index, arglist, size) else structLookup rhs x (index+1)

(* A local variable environment also knows the next unused store location *)

// ([("x",9);("y",8)],10)  
// x 在位置9,y在位置8,10--->下一个空闲空间位置10
type locEnv = int env * int

(* A function environment maps a function name to parameter list and body *)
//函数参数例子:
//void func (int a , int *p)
// 参数声明列表为: [(TypI,"a");(TypP(TypI) ,"p")]
type paramdecs = (typ * string) list

(* 函数环境列表  
  [("函数名", ([参数元组(类型,"名称")的列表],函数体AST)),....]
  
  //main (i){
  //  int r;
  //    fac (i, &r);
  //    print r;
 // }

  [ ("main",
   ([(TypI, "i")],
    Block
      [Dec (TypI,"r");
       Stmt (Expr (Call ("fac",[Access (AccVar "i"); Addr (AccVar "r")])));
       Stmt (Expr (Prim1 ("printi",Access (AccVar "r"))))]))]
*)

type funEnv = (paramdecs * stmt) env

(* A global environment consists of a global variable environment 
   and a global function environment 
 *)

// 全局环境是 变量声明环境 和 函数声明环境的元组
// 两个列表的元组
// ([var declares...],[fun declares..])
// ( [ ("x" ,1); ("y",2) ], [("main",mainAST);("fac",facAST)] )
// mainAST,facAST 分别是main 与fac 的抽象语法树

type gloEnv = int env * funEnv 

type structEnv = (string *  paramdecs * int ) list

(* The store maps addresses (ints) to values (ints): *)

//地址是store上的的索引值
type address = int

//store 是一个 地址到值的映射，是对内存的抽象
// map{(0,3);(1,8) }
// 位置0 保存了值 3
// 位置1 保存了值 8

type store = Map<address,men>

//空存储
let emptyStore = Map.empty<address,men>

//保存value到存储store
let setSto (store : store) addr value = store.Add(addr, value)

//输入addr 返回存储的值value
let getSto (store : store) addr = store.Item addr

// store上从loc开始分配n个值的空间
let rec initSto loc n store = 
    if n=0 then store else initSto (loc+1) (n-1) (setSto store loc -999)

(* Combined environment and store operations *)

(* Extend local variable environment so it maps x to nextloc 
   (the next store location) and set store[nextloc] = v.

locEnv结构是元组 : (绑定环境env,下一个空闲地址nextloc)
store结构是Map<string,int> 

扩展环境 (x nextloc) :: env ====> 新环境 (env1,nextloc+1)
变更store (nextloc) = v          
 *)

// 绑定一个值 x,v 到环境
// 返回新环境 locEnv,更新store,
// nextloc是store上下一个空闲位置
let bindVar x v (env, nextloc) store : locEnv * store = 
    let env1 = (x, nextloc) :: env 
    ((env1, nextloc + 1), setSto store nextloc v)

//将多个值 xs vs绑定到环境
//遍历 xs vs 列表,然后调用 bindVar实现单个值的绑定

let rec bindVars xs vs locEnv store : locEnv * store = 
    match (xs, vs) with 
    | ([], [])         -> (locEnv, store)
    | (x1::xr, v1::vr) -> 
      let (locEnv1, sto1) = bindVar x1 v1 locEnv store
      bindVars xr vr locEnv1 sto1
    | _ -> failwith "parameter/argument mismatch"    

(* Allocate variable (int or pointer or array): extend environment so
   that it maps variable to next available store location, and
   initialize store location(s).  
 *)
//
let rec allocate (typ, x) (env0, nextloc) structEnv sto0 : locEnv * store = 
    let (nextloc1, v, sto1) =
        match typ with
        //数组 调用initSto 分配 i 个空间
        | TypA (t, Some i) -> (nextloc+i, nextloc, initSto nextloc i sto0)
        // 默认值是 -1
        | TypS  -> (nextloc+128, nextloc, initSto nextloc 128 sto0)
        | TypeStruct s -> let (index,arg,size) = structLookup structEnv s 0
                          (nextloc+size, index, initSto nextloc size sto0)
        | _ -> (nextloc, -1, sto0)
       
    bindVar x v (env0, nextloc1) sto1

(* Build global environment of variables and functions.  For global
   variables, store locations are reserved; for global functions, just
   add to global function environment. 
*)
let allsize typ = 
    match typ with
    |  TypA (t, Some i) -> i
    |  TypS ->  128
    |  _ -> 1



(* ------------------------------------------------------------------- *)
let float2BitInt  (a:float32) :int= 
   let mutable sflag = "0"
   if a > (float32 0) then sflag = "0"
                      else sflag = "1"
   let mutable temp = int a
   let mutable tail = a-float32 (int a)
   let mutable re = ""

   let mutable count = 0;
   while temp<>0 && count<8 do
      re <-  (string (temp % 2)) + re
      count <- count+1
      temp <- temp / 2
   while count <8 do
      re<- "0"+re
      count <- count+1
   let mutable count2 = 0;
   while  tail<> float32 0  && count2<23 do
      tail <- tail+ tail
      re <- re + string( (  int tail  )%2 )
      count2 <- count2+1
      tail <- tail - (float32 (int tail))
   while count2 <23 do
      re<- re+"0"
      count2 <- count2+1
   re <- sflag + re;

   let mutable fin = 0;
   for i=0 to 31 do
        fin <- ( fin * 2 +  ( int (string (re.Chars(i))) )  );
   fin

let Int2float  (a:int) :float32= 
  
    let mutable s = "";
    let mutable temp = a;
    while temp<>0 do
      s <- (string (temp%2))  + s;
      temp <- temp/2;
    while s.Length<32 do 
      s <- "0" + s
    
    let mutable a = 1;
    let mutable main = 0;
    
    if s.Chars(0).Equals('1') then
      a <- -1;
    for i=0 to 7 do
        main <- main*2 + (int (string (s.Chars(i+1))))
    
    let mutable fina = float32 main
    let mutable pow = 0.5F

    for i=0 to 22 do
        fina <- fina+ (float32 (string (s.Chars(i+9))))*pow
        pow <- pow * 0.5F
    
    fina*(float32 a)
    

(* Interpreting micro-C statements *)
let rec exec stmt (locEnv : locEnv) (gloEnv : gloEnv)(structEnv: structEnv) (store : store) : store = 
    match stmt with
    | If(e, stmt1, stmt2) -> 
      let (v, store1) = eval e locEnv gloEnv structEnv store
      if v<>0 then exec stmt1 locEnv gloEnv structEnv store1 //True分支
              else exec stmt2 locEnv gloEnv structEnv store1 //False分支
    | While(e, body) -> 
      //定义 While循环辅助函数 loop
      let rec loop store1 =
                //求值 循环条件,注意变更环境 store
              let (v, store2) = eval e locEnv gloEnv structEnv store1
                // 继续循环
              if v<>0 then loop (exec body locEnv gloEnv structEnv store2)
                      else store2  //退出循环返回 环境store2
      loop store
    | Expr e ->
      // _ 表示丢弃e的值,返回 变更后的环境store1 
      let (_, store1) = eval e locEnv gloEnv structEnv store 
      store1 
    | Block stmts -> 
        // 语句块 解释辅助函数 loop
      let rec loop ss (locEnv, store) = 
          match ss with 
          | [ ] -> store
                             //语句块,解释 第1条语句s1
                            // 调用loop 用变更后的环境 解释后面的语句 sr.
          | s1::sr -> loop sr (stmtordec s1 locEnv gloEnv structEnv store)
      loop stmts (locEnv, store) 
    | Return e ->  match e with
                  | Some e1 -> let (res ,store0) = eval e1 locEnv gloEnv structEnv store;
                               let st = store0.Add(-1, res);
                               (st)                     
                  | None -> store
    | For(e1,e2,e3,body) -> 
          let (res ,store0) = eval e1 locEnv gloEnv structEnv store
          let rec loop store1 =
                //求值 循环条件,注意变更环境 store
              let (v, store2) = eval e2 locEnv gloEnv structEnv store1
                // 继续循环
              if v<>0 then  let (reend ,store3) = eval e3 locEnv gloEnv structEnv (exec body locEnv gloEnv structEnv store2)
                            loop store3
                      else store2  
          loop store0
    | Forin(acc,e1,e2,body) -> 
          let (loc, store1) = access acc locEnv gloEnv structEnv store
          let (re, store2) = eval e1 locEnv gloEnv structEnv store1
          let (re2,store3) = eval e2 locEnv gloEnv  structEnv store2
          match e1 with
          | CstI i -> let rec loop i stores =
                          if i<>(re2+1) then loop (i+1) (exec body locEnv gloEnv structEnv (setSto stores loc i) )
                                    else (stores)
                      loop re store3 
          | Access acc -> match acc with
                          | AccIndex(ac, idx) ->
                            let rec loop i stores =
                              match i with 
                              | Access acc2 -> match acc2 with
                                              | AccIndex(ac2, idx2) ->
                                                let ( index,stores2) = eval idx2 locEnv gloEnv structEnv stores ;
                                                if i<>e2 then let (result,s) = eval i locEnv gloEnv structEnv stores2
                                                              loop (Access (AccIndex (ac,CstI (index+1)) ) ) (exec body locEnv gloEnv structEnv (setSto s loc result) )
                                                         else let (result,s) = eval i locEnv gloEnv structEnv stores2
                                                              exec body locEnv gloEnv structEnv (setSto s loc result) 
                            loop e1 store3 
    | DoWhile(body,e) -> 
      let rec loop store1 =
                //求值 循环条件,注意变更环境 store
              let (v, store2) = eval e locEnv gloEnv structEnv store1
                // 继续循环
              if v<>0 then loop (exec body locEnv gloEnv structEnv store2)
                      else store2  //退出循环返回 环境store2
      loop (exec body locEnv gloEnv structEnv store)
    | Switch(e,body) ->  
              let (res, store1) = eval e locEnv gloEnv structEnv store
              let rec choose list =
                match list with
                | Case(e1,body1) :: tail -> 
                    let (res2, store2) = eval e1 locEnv gloEnv structEnv store1
                    if res2=res then exec body1 locEnv gloEnv structEnv store2
                                else choose tail
                | [] -> store1
                | Default( body1 ) :: tail -> 
                    exec body1 locEnv gloEnv structEnv store1
                    choose tail
              (choose body)
    | Case(e,body) -> exec body locEnv gloEnv structEnv store
    | MatchItem(e,body) ->  
              let (res, store1) = eval e locEnv gloEnv structEnv store
              let rec choose list =
                match list with
                | Pattern(e1,body1) :: tail -> 
                    let (res2, store2) = eval e1 locEnv gloEnv structEnv store1
                    if res2 = res  then exec body1 locEnv gloEnv structEnv store2
                                   else choose tail
                | [] -> store1 
                | MatchAll( body1) :: tail ->
                    exec body1 locEnv gloEnv structEnv store1
                    choose tail
              (choose body)
    | Pattern(e,body) -> exec body locEnv gloEnv  structEnv store
    | MatchAll (body )->  exec body locEnv gloEnv structEnv  store
    | DoUntil(body,e) -> 
      let rec loop store1 =
              let (v, store2) = eval e locEnv gloEnv structEnv  store1
              if v=0 then loop (exec body locEnv gloEnv structEnv  store2)
                     else store2    
      loop (exec body locEnv gloEnv structEnv store)
    | Break -> failwith("break")
    | Continue -> failwith("continue")

and stmtordec stmtordec locEnv gloEnv structEnv store = 
    match stmtordec with 
    | Stmt stmt   -> (locEnv, exec stmt locEnv gloEnv structEnv store)
    | Dec(typ, x) -> allocate (typ, x)  locEnv structEnv store
    | DeclareAndAssign(typ, x,e) -> let (loc,store1) = allocate (typ, x)  locEnv structEnv store
                                    let (loc2, store2) = access (AccVar x) loc gloEnv structEnv store1
                                    let (res, store3) = 
                                      match e with
                                      | ConstString s ->  let rec sign index stores=
                                                           if index<s.Length then
                                                              sign (index+1) ( setSto stores (loc2-index-1) (int (s.Chars(index) ) ) )
                                                            else stores  
                                                          ( s.Length   ,sign 0 store2)
                                      | _ ->  eval e loc gloEnv structEnv store2
                                    (res, setSto store3 loc2 res) 

(* Evaluating micro-C expressions *)


and typeof e locEnv gloEnv structEnv store : typ =  
    match e with  
    | ToInt e -> TypI
    | ToChar e -> TypC
    | ToFloat e -> TypF
    | CreateI(s,hex) -> TypI
    | Access acc     -> let (loc, store1) = access acc locEnv gloEnv structEnv store
                        (getSto store1 loc, store1) 
    | Self(acc,opt,e)-> let typ1 =  typeof e locEnv gloEnv structEnv store
                        match opt with
                        | "*"  ->  let res = i1 * i2
                                   (res, setSto store2 loc res)
                        | "+B"  -> let res = i1 + i2
                                   (res, setSto store2 loc res)
                        | "-B"  -> let res = i1 - i2  
                                   (res, setSto store2 loc res)
                        | "+"  ->  let res = i1 + i2
                                   (i1, setSto store2 loc res)
                        | "-"  ->  let res = i1 - i2  
                                   (i1, setSto store2 loc res)
                        | "/"  ->  let res = i1 / i2  
                                   (res, setSto store2 loc res)
                        | "%"  ->  let res = i1 % i2  
                                   (res, setSto store2 loc res)
                        | _    -> failwith ("unknown primitive " + opt) 
    | Assign(acc, e) -> typeof e locEnv gloEnv structEnv store
    | CstI i         -> TypI
    | ConstNull      -> TypI
    | ConstBool b    -> TypB
    | ConstString s  -> TypS
    | ConstFloat f   -> TypF
    | ConstChar c    -> TypC
    | Addr acc       -> match acc with 
                       | AccVar x           -> (lookup (fst locEnv) x, store)
                       | AccDeref e         -> eval e locEnv gloEnv structEnv store
                       | AccIndex(acc, idx) -> 
                              let (a, store1) = access acc locEnv gloEnv structEnv store
                              let aval = getSto store1 a
                              let (i, store2) = eval idx locEnv gloEnv structEnv store1
                              (aval + i, store2) 
                       | AccStruct(acc,acc2) ->  
                              let (a, store1) = access acc locEnv gloEnv structEnv store
                              let aval = getSto store1 a
                              let list = structEnv.[aval]
                              let param =
                                  match list with 
                                  | (string,paramdecs,int) -> paramdecs
                              let rec lookuptyp list index = 
                                  match list with
                                  | [] -> failwith("can not find ")
                                  | (typ , name ) ::tail -> match acc2 with
                                                            | AccVar x -> if x = name then typ
                                                                                      else lookuptyp tail ( index + ( allsize typ) )
                                                            | AccIndex( acc3, idx ) ->  match acc3 with
                                                                                        | AccVar y ->  if name = y then 
                                                                                                       match typ with 
                                                                                                       | TypA (arrtyp) -> arrtyp
                                                                                                       else lookuptyp tail (index + (allsize typ))                                                                                          
    | Println(acc)   -> let typ = typeof e1 locEnv gloEnv structEnv store 
                        if typ = TypS then TypS 
                                   else failwith("type error")
    | Print(op,e1)   -> let typ = typeof e1 locEnv gloEnv structEnv store 
                        match op with
                        | "%c"   -> if typ = TypC then TypC
                                                  else failwith("type error")
                        | "%d"   -> if typ = TypI then TypI
                                                  else failwith("type error")  
                        | "%f"   -> if typ = TypF then TypF
                                                  else failwith("type error")                
    | PrintHex(hex,e1)-> let typ = typeof e1 locEnv gloEnv structEnv store 
                         if typ = TypI then TypI
                                       else failwith("type error")
    | Prim1(ope, e1) ->
      let typ = typeof e1 locEnv gloEnv structEnv store
          match (ope, typ) with
          | ("!" ,TypI)    -> TypB
          | ("!" ,TypB)    -> TypB
          | _        -> failwith ("unknown primitive " + ope) 
    | Prim2(ope, e1, e2) ->
      let typ1 = typeof e1 locEnv gloEnv structEnv store
      let typ2 = typeof e2 locEnv gloEnv structEnv store
      match (ope, typ1, typ2) with  
      | ("*",  TypI, TypI) -> TypI  
      | ("+",  TypI, TypI) -> TypI
      | ("+",  TypF, TypF) -> TypF  
      | ("+",  TypI, TypC) -> TypC
      | ("+",  TypC, TypI) -> TypC  
      | ("-",  TypI, TypI) -> TypI
      | ("-",  TypF, TypF) -> TypF  
      | ("-",  TypC, TypI) -> TypC
      | ("==", TypI, TypI) -> TypB
      | ("==", TypB, TypB) -> TypB  
      | ("==", TypC, TypC) -> TypB 
      | ("==", TypF, TypF) -> TypB
      | ("!=", TypI, TypI) -> TypB
      | ("!=", TypB, TypB) -> TypB  
      | ("!=", TypC, TypC) -> TypB 
      | ("!=", TypF, TypF) -> TypB  
      | ("<=", TypI, TypI) -> TypB
      | ("<=", TypB, TypB) -> TypB  
      | ("<=", TypC, TypC) -> TypB 
      | ("<=", TypF, TypF) -> TypB 
      | ("<", TypI, TypI) -> TypB
      | ("<", TypB, TypB) -> TypB  
      | ("<", TypC, TypC) -> TypB 
      | ("<", TypF, TypF) -> TypB 
      | (">=", TypI, TypI) -> TypB
      | (">=", TypB, TypB) -> TypB  
      | (">=", TypC, TypC) -> TypB 
      | (">=", TypF, TypF) -> TypB 
      | (">", TypI, TypI) -> TypB
      | (">", TypB, TypB) -> TypB  
      | (">", TypC, TypC) -> TypB 
      | (">", TypF, TypF) -> TypB 
      | _   -> failwith "unknown primitive, or type error"  
    | Prim3( e1, e2 , e3) ->
         let typ1 = typeof e1 locEnv gloEnv structEnv store
         let typ2 = typeof e2 locEnv gloEnv structEnv store
         let typ3 = typeof e2 locEnv gloEnv structEnv store
         if typ1 = TypB then 
                        let (i1, store1) = eval e1 locEnv gloEnv structEnv store
                        if i1 <> 0 then typ3
                                  else typ2
                        else typ3
         elif typ1 = TypI then 
                        let (i1, store1) = eval e1 locEnv gloEnv structEnv store
                        if i1 <> 0 then typ3
                                  else typ2
         else failwith("type error")
    | Andalso(e1, e2) ->
       let typ1 = typeof e1 locEnv gloEnv structEnv store
       let typ2 = typeof e2 locEnv gloEnv structEnv store 
       match (typ1,typ2) with
       | (TypB,TypB) -> TypB
       | (TypI,TypI) -> TypB
       | (TypB,TypI) -> TypB
       | (TypI,TypB) -> TypB
    | Orelse(e1, e2) -> 
        let typ1 = typeof e1 locEnv gloEnv structEnv store
        let typ2 = typeof e2 locEnv gloEnv structEnv store 
        match (typ1,typ2) with
        | (TypB,TypB) -> TypB
        | (TypI,TypI) -> TypB
        | (TypB,TypI) -> TypB
        | (TypI,TypB) -> TypB
    | Call(f, es) ->  TypI
    

and eval e locEnv gloEnv structEnv store : int  * store = 

    match e with
    | ToInt e -> match e with
                 | ConstChar c -> (int c - int '0',store)
                 | ConstFloat f -> (int f,store)
                 | _ -> eval e locEnv gloEnv structEnv store
    | ToChar e -> match e with
                 | CstI i -> (i + int '0',store)
                 | _ -> eval e locEnv gloEnv structEnv store
    | ToFloat e -> match e with
                 | CstI i -> (float2BitInt( float32 i),store)
                 | _ -> eval e locEnv gloEnv structEnv store
    | CreateI(s,hex) -> let mutable res = 0;
                        for i=0 to s.Length-1 do
                           if s.Chars(i)>='0' && s.Chars(i)<='9' then
                             res <- res*hex + ( (int (s.Chars(i)))-(int '0') )
                           elif s.Chars(i)>='a' && s.Chars(i)<='f' then
                             res <- res*hex + ( (int (s.Chars(i)))-(int 'a')+10 )
                           elif s.Chars(i)>='A' && s.Chars(i)<='F' then
                             res <- res*hex + ( (int (s.Chars(i)))-(int 'A')+10 )
                           else 
                             failwith("ERROR WORLD IN NUMBER")
                        (res,store)
    | Access acc     -> let (loc, store1) = access acc locEnv gloEnv structEnv store
                        (getSto store1 loc, store1) 
    | Self(acc,opt,e)-> let (loc, store1) = access acc locEnv gloEnv structEnv store
                        let (i1) = getSto store1 loc
                        let (i2, store2) = eval e locEnv gloEnv structEnv store
                        match opt with
                        | "*"  ->  let res = i1 * i2
                                   (res, setSto store2 loc res)
                        | "+B"  -> let res = i1 + i2
                                   (res, setSto store2 loc res)
                        | "-B"  -> let res = i1 - i2  
                                   (res, setSto store2 loc res)
                        | "+"  ->  let res = i1 + i2
                                   (i1, setSto store2 loc res)
                        | "-"  ->  let res = i1 - i2  
                                   (i1, setSto store2 loc res)
                        | "/"  ->  let res = i1 / i2  
                                   (res, setSto store2 loc res)
                        | "%"  ->  let res = i1 % i2  
                                   (res, setSto store2 loc res)
                        | _    -> failwith ("unknown primitive " + opt)
                       
    | Assign(acc, e) -> let (loc, store1) = access acc locEnv gloEnv structEnv store
                        let (res,store2)= 
                          match e with
                          | ConstString s -> let rec sign index stores=
                                                if index<s.Length then
                                                  sign (index+1) ( setSto stores (loc-index-1) (int (s.Chars(index) ) ) )
                                                else stores  
                                             ( s.Length   ,sign 0 store1)
                          | _ ->  eval e locEnv gloEnv structEnv store1
                        (res, setSto store2 loc res) 
    | CstI i         -> (i, store)
    | ConstNull      -> (0 ,store)
    | ConstBool b    -> let res  = 
                            if b = false then 0 
                                         else 1
                        (res,store)
    | ConstString s  -> (s.Length,store)
    | ConstFloat f   -> (float2BitInt f,store)
    | ConstChar c    -> ((int c), store)
    | Addr acc       -> access acc locEnv gloEnv structEnv store
    | Println(acc)   -> let (loc, store1) = access acc locEnv gloEnv structEnv store
                        let (i1) = getSto store1 loc
                        for i= 0 to i1-1 do
                            let i2 = getSto store1 (loc-i-1)
                            (printf "%c" (char i2))
                        (printf "\n" )
                        (i1,store1)
    | Print(op,e1)   -> let (i1, store1) = eval e1 locEnv gloEnv structEnv store
                        let res = 
                          match op with
                          | "%c"   -> (printf "%c " (char i1); i1)
                          | "%d"   -> (printf "%d " i1; i1)  
                          | "%f"   -> (printf "%f " (Int2float(i1));i1 )
                        (res, store1)  
    | PrintHex(hex,e1)->let (i1, store1) = eval e1 locEnv gloEnv structEnv store
                        let mutable temp = i1
                        let mutable s  = ""
                        while temp>0 do
                           if temp%hex>=0 && temp%hex<=9  then
                              s <-  ( string ( temp % hex ) ) + s;
                              temp <- temp/hex;
                           elif  temp%hex>9  then 
                              s <-  string ( char ((  temp % hex   )+55) ) + s;
                              temp <- temp/hex;
                        printf "%s " s ;
                        (i1, store1)         
    | Prim1(ope, e1) ->
      let (i1, store1) = eval e1 locEnv gloEnv structEnv store
      let res =
          match ope with
          | "!"      -> if i1=0 then 1 else 0
          | _        -> failwith ("unknown primitive " + ope)
      (res, store1) 
    | Prim2(ope, e1, e2) ->
      let (i1, store1) = eval e1 locEnv gloEnv structEnv store
      let (i2, store2) = eval e2 locEnv gloEnv structEnv store1
      let res =
          match ope with
          | "*"  -> i1 * i2
          | "+"  -> i1 + i2
          | "-"  -> i1 - i2
          | "/"  -> i1 / i2
          | "%"  -> i1 % i2
          | "==" -> if i1 =  i2 then 1 else 0
          | "!=" -> if i1 <> i2 then 1 else 0
          | "<"  -> if i1 <  i2 then 1 else 0
          | "<=" -> if i1 <= i2 then 1 else 0
          | ">=" -> if i1 >= i2 then 1 else 0
          | ">"  -> if i1 >  i2 then 1 else 0
          | _    -> failwith ("unknown primitive " + ope)
      (res, store2) 
    | Prim3( e1, e2 , e3) ->
        let (i1, store1) = eval e1 locEnv gloEnv structEnv store
        let (i2, store2) = eval e2 locEnv gloEnv structEnv store1
        let (i3, store3) = eval e3 locEnv gloEnv structEnv store2
        if i1 = 0 then (i2,store3) 
                  else (i3,store3)  
    | Andalso(e1, e2) -> 
      let (i1, store1) as res = eval e1 locEnv gloEnv structEnv store
      if i1<>0 then eval e2 locEnv gloEnv structEnv store1 else res
    | Orelse(e1, e2) -> 
      let (i1, store1) as res = eval e1 locEnv gloEnv structEnv store
      if i1<>0 then res else eval e2 locEnv gloEnv structEnv store1
    | Call(f, es) -> callfun f es locEnv gloEnv structEnv store 


and access acc locEnv gloEnv structEnv store : int * store = 
    match acc with 
    | AccVar x           -> (lookup (fst locEnv) x, store)
    | AccDeref e         -> eval e locEnv gloEnv structEnv store
    | AccIndex(acc, idx) -> 
      let (a, store1) = access acc locEnv gloEnv structEnv store
      let aval = getSto store1 a
      let (i, store2) = eval idx locEnv gloEnv structEnv store1
      (aval + i, store2) 
    | AccStruct(acc,acc2) ->  let (a, store1) = access acc locEnv gloEnv structEnv store
                              let aval = getSto store1 a
                              let list = structEnv.[aval]
                              let param =
                                  match list with 
                                  | (string,paramdecs,int) -> paramdecs
                              let rec lookupidx list index = 
                                  match list with
                                  | [] -> failwith("can not find ")
                                  | (typ , name ) ::tail -> match acc2 with
                                                            | AccVar x -> if x = name then ( index + ( allsize typ ) )
                                                                                      else lookupidx tail ( index + ( allsize typ) )
                                                            | AccIndex( acc3, idx ) ->  match acc3 with
                                                                                        | AccVar y ->  if name = y then 
                                                                                                       let (i, store2) = eval idx locEnv gloEnv structEnv store1
                                                                                                       (index + i)
                                                                                                       else lookupidx tail (index + (allsize typ))
                              ((a+(lookupidx param 0)),store1)

and evals es locEnv gloEnv structEnv store : int list * store = 
    match es with 
    | []     -> ([], store)
    | e1::er ->
      let (v1, store1) = eval e1 locEnv gloEnv structEnv store
      let (vr, storer) = evals er locEnv gloEnv structEnv store1 
      (v1::vr, storer) 
    
and callfun f es locEnv gloEnv structEnv store : int * store =

    info (fun () -> printf "callfun: %A\n"  (f, locEnv, gloEnv,structEnv,store))

    let (_, nextloc) = locEnv
    let (varEnv, funEnv) = gloEnv
    let (paramdecs, fBody) = lookup funEnv f
    let (vs, store1) = evals es locEnv gloEnv structEnv store
    let (fBodyEnv, store2) = 
        bindVars (List.map snd paramdecs) vs (varEnv, nextloc) store1
    let store3 = exec fBody fBodyEnv gloEnv structEnv store2
    let res = store3.TryFind(-1) 
    let restore = store3.Remove(-1)
    match res with
    | None -> (0,restore)
    | Some i -> (i,restore)

(* Interpret a complete micro-C program by initializing the store 
   and global environments, then invoking its `main' function.
 *)
 


let initEnvAndStore (topdecs : topdec list) : locEnv * funEnv * structEnv * store = 
    
    //包括全局函数和全局变量
    info (fun () -> printf "topdecs:%A\n" topdecs)

    let rec addv decs locEnv funEnv structEnv store = 
        match decs with 
        | [] -> (locEnv, funEnv,structEnv, store)
        
        // 全局变量声明  调用allocate 在store上给变量分配空间
        | Vardec (typ, x) :: decr -> 
          let (locEnv1, sto1) = allocate (typ, x) locEnv structEnv store
          addv decr locEnv1 funEnv structEnv sto1 
        | Fundec (_, f, xs, body) :: decr ->
          addv decr locEnv ((f, (xs, body)) :: funEnv) structEnv store
        | Structdec (name,list) :: decr ->
          let rec sizeof list all = 
            match list with
            | [] -> all
            | ( typ ,string ):: tail -> sizeof tail ((allsize typ) + all)
          let fin = sizeof list 0
          addv decr locEnv funEnv ((name,list, fin) :: structEnv) store
        | VariableDeclareAndAssign (typ,x,e) :: decr ->
          let (locEnv1, sto1) = allocate (typ, x) locEnv structEnv store
          addv decr locEnv1 funEnv structEnv sto1 
          
    
    // ([], 0) []  默认全局环境 
    // locEnv ([],0) 变量环境 ，变量定义为空列表[],下一个空闲地址为0
    // ([("n", 1); ("r", 0)], 2)  表示定义了 变量 n , r 下一个可以用的变量索引是 2
    // funEnv []   函数环境，函数定义为空列表[]
    addv topdecs ([], 0) [] [] emptyStore



// run 返回的结果是 代表内存更改的 store 类型
// vs 参数列表 [8,2,...]
// 可以为空 []
let run (Prog topdecs) vs = 
    //
    let ((varEnv, nextloc), funEnv, structEnv,store0) = initEnvAndStore topdecs
    
    // mainParams 是 main 的参数列表
    //
    let (mainParams, mainBody) = lookup funEnv "main"
    
    let (mainBodyEnv, store1) = 
        bindVars (List.map snd mainParams) vs (varEnv, nextloc)  store0


    info ( fun () ->
        
        //以ex9.c为例子 
        // main的 AST
        printf "\nmainBody:\n %A\n" mainBody
        
        //局部环境  
        // 如
        // i 存储在store位置0,store中下个空闲位置是1
        //([("i", 0)], 1)

        printf "\nmainBodyEnv:\n %A\n"  mainBodyEnv
        
        //全局环境 (变量,函数定义)
        // fac 的AST
        // main的 AST
        printf $"\n varEnv:\n {varEnv} \nfunEnv:\n{funEnv}\n" 
        
        //当前存储 
        // store 中 0 号 位置存储值为8
        // map [(0, 8)]
        printf "\nstore1:\n %A\n" store1 
    )

    exec mainBody mainBodyEnv (varEnv, funEnv) structEnv store1

(* Example programs are found in the files ex1.c, ex2.c, etc *)
