using RaLanguage.Errors;
using RaLanguage.Errors.Types;
using RaLanguage.Lexer;
using RaLanguage.Lexer.Tokens;
using RaLanguage.Parser.Nodes;
using RaLanguage.Parser.Nodes.Annotations;
using RaLanguage.Parser.Nodes.Classes;
using RaLanguage.Parser.Nodes.Enums;
using RaLanguage.Parser.Nodes.Functions;
using RaLanguage.Parser.Nodes.Interfaces;
using RaLanguage.Parser.Nodes.Iterations;
using RaLanguage.Parser.Nodes.Operations;
using RaLanguage.Parser.Nodes.Primitives;
using RaLanguage.Parser.Nodes.Special;
using RaLanguage.Parser.Nodes.Statements;
using RaLanguage.Parser.Nodes.Structs;
using RaLanguage.Parser.Nodes.Traits;
using RaLanguage.Parser.Nodes.Variables;
using RaLanguage.Parser.Nodes.Imports;
using RaLanguage.Parser.Nodes.Namespaces;
using RaLanguage.Parser.Nodes.Records;
using RaLanguage.Types;

namespace RaLanguage.Parser
{
    public partial class Parser
    {
        private ParserResult ParseExtensionDefinition()
        {
            var res = new ParserResult();
            bool isPublic = false;

            if (_currentToken.Matches(Keyword.Pub))
            {
                isPublic = true;
                res.RegisterAdvancement();
                Advance();
            }

            if (!_currentToken.Matches(Keyword.Extend))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "extend",
                    context: "to start an extension block",
                    help: "extension syntax: 'extend TargetType { fn ... }'"));

            res.RegisterAdvancement();
            Advance();

            var targetType = ParseType(res);
            if (targetType == null)
                return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "'extend'"));

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the extension body"));

            res.RegisterAdvancement();
            Advance();

            var methods = new List<FunctionDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                bool methodPublic = false;
                if (_currentToken.Matches(Keyword.Pub))
                {
                    methodPublic = true;
                    res.RegisterAdvancement();
                    Advance();
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                        context: "to declare an extension method",
                        help: "only 'fn' declarations are allowed inside an extension body"));

                var fnRes = ParseFunctionDefinition(isPublic: methodPublic);
                if (fnRes.Error != null) return fnRes;

                var fnNode = (FunctionDefinitionNode)fnRes.Node!;

                if (fnNode.IsConstructor)
                    return res.Failure(ParserDiagnostics.ExtensionConstructorNotAllowed(fnNode.PositionStart, fnNode.PositionEnd));

                if (fnNode.IsAbstract)
                    return res.Failure(ParserDiagnostics.ExtensionMethodNeedsBody(fnNode.PositionStart, fnNode.PositionEnd));

                if (fnNode.BodyNode == null)
                    return res.Failure(ParserDiagnostics.ExtensionMethodNeedsBody(fnNode.PositionStart, fnNode.PositionEnd));

                methods.Add(fnNode);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new ExtensionDefinitionNode(targetType, isPublic, methods));
        }


        private ParserResult ParseCallableSignatureAfterName(bool allowReturnType)
        {
            var res = new ParserResult();

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var isRefParams = new List<bool>();
            var paramDefaults = new List<AstNode?>();

            bool hasVarArgs = false;
            Token? varArgNameTok = null;
            TypeDescriptor? varArgType = null;
            TypeDescriptor? returnType = null;

            bool sawDefault = false;

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the parameter list"));

            res.RegisterAdvancement();
            Advance();

            if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.SPREAD || _currentToken.Matches(Keyword.Ref))
            {
                while (true)
                {
                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        hasVarArgs = true;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                                after: "'...'",
                                help: "variadic parameters take an identifier, e.g. '...args: int'"));

                        varArgNameTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedVarArgsType(_currentToken));

                            varArgType = parsed;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.VariadicMustBeLast(_currentToken.PositionStart, _currentToken.PositionEnd));

                        break;
                    }

                    bool isRef = false;
                    if (_currentToken.Matches(Keyword.Ref))
                    {
                        isRef = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken, hostingConstruct: "parameter list"));

                    var paramTok = _currentToken;
                    argNameToks.Add(paramTok);
                    res.RegisterAdvancement();
                    Advance();

                    TypeDescriptor? ptype = null;
                    if (_currentToken.Type == TokenType.COLON)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        var parsed = ParseType(res);
                        if (parsed == null)
                            return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "a parameter declaration"));

                        if (isRef)
                        {
                            ptype = TypeDescriptor.RefType(parsed);
                        }
                        else
                        {
                            ptype = parsed;
                        }
                    }
                    argTypes.Add(ptype);
                    isRefParams.Add(isRef);

                    AstNode? defaultExpr = null;
                    if (_currentToken.Type == TokenType.EQ)
                    {
                        sawDefault = true;
                        res.RegisterAdvancement();
                        Advance();

                        defaultExpr = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                    }
                    else if (sawDefault)
                    {
                        return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                    }

                    paramDefaults.Add(defaultExpr);

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        if (_currentToken.Type == TokenType.RPAREN) break;
                        continue;
                    }

                    break;
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or ')'",
                        contextHint: "parameter lists are comma-separated and end with ')'"));
            }
            else
            {
                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a parameter name or ')'",
                        contextHint: "parameter lists begin with names or '...' and end with ')'"));
            }

            res.RegisterAdvancement();
            Advance();

            if (allowReturnType && _currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                var parsed = ParseType(res);
                if (parsed == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                returnType = parsed;
            }

            return res.Success(new CallableSignatureNode(
                argNameToks,
                argTypes,
                isRefParams,
                paramDefaults,
                hasVarArgs,
                varArgNameTok,
                varArgType,
                returnType
            ));
        }


        private ParserResult ParseTraitDefinition(bool isPublic)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Trait))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "trait",
                    context: "to start a trait declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                    after: "'trait'",
                    help: "trait declarations begin with a name, e.g. 'trait Printable { ... }'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{', context: "the trait body"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var methods = new List<TraitMethodDefinitionNode>();
            var fields = new List<StructFieldDefinitionNode>();
            var traitProperties = new List<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;
                bool isAbstract = false;

                if (_currentToken.Matches(Keyword.Pub))
                {
                    res.RegisterAdvancement();
                    Advance();

                    memberPublic = true;

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseTraitFieldDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var (nameTokh, defaultValueNode, typeNode) = d;
                        var fieldNode = new StructFieldDefinitionNode(
                            memberPublic,
                            nameTokh,
                            typeNode,
                            defaultValueNode,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Abstract))
                {
                    isAbstract = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                // Property requirement / default inside a trait body.
                // Traits inherit the same "abstract by default" rule as
                // interfaces, but a property declared with an accessor
                // body provides a default implementation that
                // implementers can inherit (mirroring trait method
                // defaults).
                if (_currentToken.Matches(Keyword.Prop))
                {
                    bool traitIsAbstract = isAbstract;
                    var propRes = ParsePropertyDeclaration(
                        isPublic: memberPublic,
                        isStatic: false,
                        isAbstract: traitIsAbstract,
                        isOverride: false,
                        isLazy: false);
                    if (propRes.Error != null) return res.Failure(propRes.Error);
                    var propNode = (RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode)propRes.Node!;
                    AnnotationAttacher.Attach(propNode, memberAnnotations);
                    traitProperties.Add(propNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    continue;
                }

                bool traitMemberAsync = false;
                bool traitMemberAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    traitMemberAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        traitMemberAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                        context: "to declare a trait method",
                        help: "trait bodies contain field declarations or 'fn' method signatures"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "'fn'",
                        help: "every method declaration needs a name following 'fn'"));

                var methodNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var sigRes = ParseCallableSignatureAfterName(true);
                if (sigRes.Error != null) return sigRes;
                var sigNode = (CallableSignatureNode)sigRes.Node!;

                AstNode? bodyNode = null;

                if (_currentToken.Type == TokenType.ARROW)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    bodyNode = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                }
                else if (_currentToken.Type == TokenType.LBRACKET)
                {
                    res.RegisterAdvancement();
                    Advance();

                    bodyNode = res.Register(ParseStatements());
                    if (res.Error != null) return res;

                    if (_currentToken.Type != TokenType.RBRACKET)
                        return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the trait method body"));

                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    bodyNode = null;
                }

                var traitMethodNode = new TraitMethodDefinitionNode(
                    methodNameTok,
                    sigNode.ArgNameToks,
                    sigNode.ArgTypes,
                    sigNode.IsRefParams,
                    sigNode.ParamDefaults,
                    sigNode.HasVarArgs,
                    sigNode.VarArgNameTok,
                    sigNode.VarArgType,
                    sigNode.ReturnType,
                    bodyNode,
                    bodyNode != null,
                    isAbstract
                );
                traitMethodNode.IsAsync = traitMemberAsync;
                traitMethodNode.IsAsyncStream = traitMemberAsyncStream;
                AnnotationAttacher.Attach(traitMethodNode, memberAnnotations);
                methods.Add(traitMethodNode);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new TraitDefinitionNode(nameTok, isPublic, methods, fields, genericTypeParams, whereConstraints, traitProperties));
            }
            finally
            {
                PopGenericScope();
            }
        }

        private ParserResult ParseInterfaceDefinition(bool isPublic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'interface'", help: "interface declarations begin with a name, e.g. 'interface Drawable { fn draw(); }'"));

            Token nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var methods = new List<InterfaceMethodSignatureNode>();
            var fields = new List<StructFieldDefinitionNode>();
            var interfaceProperties = new List<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;
                if (_currentToken.Matches(Keyword.Pub))
                {
                    res.RegisterAdvancement();
                    Advance();
                    memberPublic = true;

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseInterfaceFieldDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var (nameTokh, typeNode, _) = d;
                        var fieldNode = new StructFieldDefinitionNode(
                            memberPublic,
                            nameTokh,
                            d.Item3,
                            typeNode,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                // Property contract inside an interface body. Interface
                // properties are abstract by definition (no storage,
                // accessor signatures only). Author with explicit
                // accessor list: `prop name: T { get; set; }`.
                if (_currentToken.Matches(Keyword.Prop))
                {
                    var propRes = ParsePropertyDeclaration(
                        isPublic: memberPublic,
                        isStatic: false,
                        isAbstract: true,
                        isOverride: false,
                        isLazy: false);
                    if (propRes.Error != null) return res.Failure(propRes.Error);
                    var propNode = (RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode)propRes.Node!;
                    AnnotationAttacher.Attach(propNode, memberAnnotations);
                    interfaceProperties.Add(propNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    continue;
                }

                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'fn' (for methods), 'prop' (for property contracts) or a field declaration",
                        contextHint: "interface bodies contain method signatures ('fn ...'), property contracts ('prop ...'), or field declarations ('var', 'let', 'const', 'final')"));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'fn'", help: "every method declaration needs a name following 'fn'"));

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                Token methodNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var argNameToks = new List<Token>();
                var argTypes = new List<TypeDescriptor?>();

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    while (true)
                    {
                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        var argTok = _currentToken;
                        argNameToks.Add(argTok);

                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        TypeDescriptor? argType = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsedType = ParseType(res);
                            if (parsedType == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));

                            argType = parsedType;
                        }

                        argTypes.Add(argType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    if (_currentToken.Type != TokenType.RPAREN)
                        return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or ')'", contextHint: "the parameter / argument list is comma-separated and ends with ')'"));
                }

                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                TypeDescriptor? returnType = null;
                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                    returnType = parsedType;
                }

                var ifaceMethodNode = new InterfaceMethodSignatureNode(methodNameTok, argNameToks, argTypes, returnType);
                AnnotationAttacher.Attach(ifaceMethodNode, memberAnnotations);
                methods.Add(ifaceMethodNode);

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new InterfaceDefinitionNode(nameTok, isPublic, methods, fields, genericTypeParams, whereConstraints, interfaceProperties));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParserPubDefinition()
        {
            var res = new ParserResult();
            bool isAbstract = false;
            bool isStatic = false;

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Matches(Keyword.Abstract) || _currentToken.Matches(Keyword.Static))
            {
                if (_currentToken.Matches(Keyword.Abstract))
                {
                    isAbstract = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Static))
                {
                    isStatic = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }
            }

            if (_currentToken.Matches(Keyword.Struct))
            {
                var structDef = res.Register(ParseStructDefinition(true));
                if (res.Error != null) return res;
                return res.Success(structDef);
            }
            else if (_currentToken.Matches(Keyword.Record))
            {
                var recordDef = res.Register(ParseRecordDefinition(isPublic: true, isAbstract: isAbstract));
                if (res.Error != null) return res;
                return res.Success(recordDef);
            }
            else if (_currentToken.Matches(Keyword.Class))
            {
                var classDef = res.Register(ParseClassDefinition(true, isAbstract, isStatic));
                if (res.Error != null) return res;
                return res.Success(classDef);
            }
            else if (_currentToken.Matches(Keyword.Interface))
            {
                var interfaceDef = res.Register(ParseInterfaceDefinition(true));
                if (res.Error != null) return res;
                return res.Success(interfaceDef);
            }
            else if (_currentToken.Matches(Keyword.Trait))
            {
                var traitDef = res.Register(ParseTraitDefinition(true));
                if (res.Error != null) return res;
                return res.Success(traitDef);
            }
            else if (_currentToken.Matches(Keyword.Fn))
            {
                var funcDef = res.Register(ParseFunctionDefinition(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(funcDef);
            }
            else if (_currentToken.Matches(Keyword.Async))
            {
                var asyncDef = res.Register(ParseAsyncFunctionDefinition(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(asyncDef);
            }
            else if (_currentToken.Matches(Keyword.Var) || _currentToken.Matches(Keyword.Final) || _currentToken.Matches(Keyword.Let) || _currentToken.Matches(Keyword.Const))
            {
                var variableDecl = res.Register(ParseVariableDeclaration(isPublic: true));
                if (res.Error != null) return res;
                return res.Success(variableDecl);
            }
            else if (_currentToken.Matches(Keyword.Annotation))
            {
                var annDef = res.Register(ParseAnnotationDefinition(true));
                if (res.Error != null) return res;
                return res.Success(annDef);
            }

            return res.Failure(ParserDiagnostics.ExpectedOneOfKeywords(_currentToken, new[] { "struct", "class" }, context: "after the access / modifier list"));
        }


        private ParserResult ParseClassDefinition(bool isPublic, bool isAbstract, bool isStatic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'class'", help: "class declarations begin with a name, e.g. 'class Point { ... }'"));

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var nameTok = _currentToken;
            string className = nameTok.Value?.ToString() ?? "";
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            TypeDescriptor? baseType = null;
            var implementedInterfaces = new List<TypeDescriptor>();
            var withTraits = new List<TypeDescriptor>();
            List<WhereConstraintNode> whereConstraints = new List<WhereConstraintNode>();

            while (_currentToken.Type != TokenType.LBRACKET)
            {
                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    baseType = ParseType(res);
                    if (baseType == null)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "':'", help: "the ':' in a class header is followed by the base class name"));

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.With))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (true)
                    {
                        var ifaceType = ParseType(res);
                        if (ifaceType == null)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'impl'", help: "list the implemented interface(s) after 'impl', e.g. 'class C impl I1, I2 { ... }'"));

                        withTraits.Add(ifaceType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    continue;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Impl))
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    while (true)
                    {
                        var ifaceType = ParseType(res);
                        if (ifaceType == null)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'impl'", help: "list the implemented interface(s) after 'impl', e.g. 'class C impl I1, I2 { ... }'"));

                        implementedInterfaces.Add(ifaceType);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            continue;
                        }

                        break;
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Where))
                {
                    res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
                    if (res.Error != null) return res;
                    continue;
                }

                if (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "':' (base class), 'impl' (interfaces), 'where' (constraints) or '{' (body)",
                    contextHint: "after a class name you may declare a base class with ':', interfaces with 'impl', constraints with 'where', or open the body with '{'"));
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            var fields = new List<StructFieldDefinitionNode>();
            var methods = new List<FunctionDefinitionNode>();
            var operators = new List<RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode>();
            var properties = new List<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool isMemberPublic = false,
                    isMemberOverride = false,
                    isMemberAbstract = false,
                    isMemberStatic = false;

                while (_currentToken.Matches(Keyword.Pub) || _currentToken.Matches(Keyword.Override) || _currentToken.Matches(Keyword.Abstract) || _currentToken.Matches(Keyword.Static))
                {
                    if (_currentToken.Matches(Keyword.Pub))
                    {
                        if (isMemberPublic)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("pub", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberPublic = true;
                    }

                    if (_currentToken.Matches(Keyword.Override))
                    {
                        if (isMemberOverride)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("override", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberOverride = true;
                    }

                    if (_currentToken.Matches(Keyword.Abstract))
                    {
                        if (isMemberAbstract)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("abstract", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberAbstract = true;
                    }

                    if (_currentToken.Matches(Keyword.Static))
                    {
                        if (isMemberStatic)
                        {
                            return res.Failure(ParserDiagnostics.DuplicateModifier("static", _currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        isMemberStatic = true;
                    }

                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseVariableDeclaration(isMemberPublic, isMemberStatic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var fieldNode = new StructFieldDefinitionNode(
                            isMemberPublic,
                            d.Item1,
                            d.Item3,
                            d.Item2,
                            isMemberStatic,
                            isMemberAbstract,
                            isMemberOverride,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                // `lazy prop` and `prop` — property declarations sit
                // here in the modifier-parsed slot, after the field
                // shortcut and before async/fn/operator. The `lazy`
                // keyword is meaningful only as a `prop` prefix; if it
                // appears alone the parser will surface the mismatch
                // below.
                bool isMemberLazy = false;
                if (_currentToken.Matches(Keyword.Lazy))
                {
                    isMemberLazy = true;
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Prop))
                {
                    var propRes = ParsePropertyDeclaration(
                        isPublic: isMemberPublic,
                        isStatic: isMemberStatic,
                        isAbstract: isMemberAbstract,
                        isOverride: isMemberOverride,
                        isLazy: isMemberLazy);
                    if (propRes.Error != null) return res.Failure(propRes.Error);

                    var propNode = (RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode)propRes.Node!;
                    AnnotationAttacher.Attach(propNode, memberAnnotations);
                    properties.Add(propNode);

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    continue;
                }

                if (isMemberLazy)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'prop' (after 'lazy')",
                        contextHint: "'lazy' is only meaningful as a 'prop' prefix; remove it or follow it with a property declaration"));
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool isMemberAsync = false;
                bool isMemberAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    isMemberAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        isMemberAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (_currentToken.Matches(Keyword.Fn) || (_currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == className))
                {
                    var fnRes = ParseFunctionDefinition(ownerTypeName: className, isPublic: isMemberPublic, isOverride: isMemberOverride, isAbstract: isMemberAbstract, isStatic: isMemberStatic, isDeclaringConstructor: _currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == className, isAsync: isMemberAsync, isAsyncStream: isMemberAsyncStream);
                    if (fnRes.Error != null) return fnRes;

                    var methodNode = (FunctionDefinitionNode)fnRes.Node!;
                    AnnotationAttacher.Attach(methodNode, memberAnnotations);
                    methods.Add(methodNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Operator))
                {
                    var opRes = ParseOperatorDefinition(isPublic: isMemberPublic, isOverride: isMemberOverride, isStatic: isMemberStatic, ownerTypeName: className);
                    if (opRes.Error != null) return opRes;

                    var opNode = (RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode)opRes.Node!;
                    AnnotationAttacher.Attach(opNode, memberAnnotations);
                    operators.Add(opNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a field declaration ('var' / 'let' / 'const' / 'final'), a method ('fn') or an operator overload ('operator')",
                    contextHint: "class / struct bodies allow only fields, methods and operator overloads"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new ClassDefinitionNode(nameTok, isPublic, isAbstract, isStatic, baseType, implementedInterfaces, withTraits, fields, methods, operators, genericTypeParams, whereConstraints, properties));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParseEnumDefinition()
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Enum))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "enum", context: "to start an enum declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'enum'", help: "enum declarations begin with a name, e.g. 'enum Color { Red, Green, Blue }'"));

            Token nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var variants = new List<EnumVariantSpec>();

            if (_currentToken.Type != TokenType.RBRACKET)
            {
                while (true)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type != TokenType.IDENTIFIER)
                        return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "previous enum member or '{'", help: "enum variants are comma-separated identifiers, optionally with a payload (e.g. 'enum Token { Eof, Number(int) }')"));

                    Token memberTok = _currentToken;
                    res.RegisterAdvancement();
                    Advance();

                    // Optional payload `(Type1, Type2, ...)` for ADT variants.
                    // Generic parameters of the enum are in scope here.
                    List<TypeDescriptor>? payloadTypes = null;
                    if (_currentToken.Type == TokenType.LPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        payloadTypes = new List<TypeDescriptor>();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                        {
                            while (true)
                            {
                                var ty = ParseType(res);
                                if (ty == null)
                                {
                                    return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: $"'{memberTok.Value}(' in enum variant payload"));
                                }
                                payloadTypes.Add(ty);

                                while (_currentToken.Type == TokenType.NEWLINE)
                                {
                                    res.RegisterAdvancement();
                                    Advance();
                                }

                                if (_currentToken.Type == TokenType.COMMA)
                                {
                                    res.RegisterAdvancement();
                                    Advance();
                                    while (_currentToken.Type == TokenType.NEWLINE)
                                    {
                                        res.RegisterAdvancement();
                                        Advance();
                                    }
                                    continue;
                                }

                                break;
                            }
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));

                        res.RegisterAdvancement();
                        Advance();

                        if (payloadTypes.Count == 0)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                                $"at least one type inside '{memberTok.Value}(...)'",
                                contextHint: "use a bare identifier (e.g. 'Eof') for zero-arity variants"));
                    }

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    AstNode? valueNode = null;
                    if (_currentToken.Type == TokenType.EQ)
                    {
                        if (payloadTypes != null)
                            return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                                "a comma or '}'",
                                contextHint: "a payload-carrying variant cannot have an explicit integer value"));

                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        valueNode = res.Register(ParseExpression());
                        if (res.Error != null) return res;
                    }

                    variants.Add(new EnumVariantSpec(memberTok, valueNode, payloadTypes));

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.COMMA)
                    {
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.RBRACKET)
                            break;

                        continue;
                    }

                    break;
                }
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new EnumDefinitionNode(nameTok, variants, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParseOperatorDefinition(bool isPublic = false, bool isOverride = false, bool isStatic = false, string? ownerTypeName = null)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            TokenType opType = _currentToken.Type;
            Keyword? opKeyword = null;
            
            if (_currentToken.Type == TokenType.KEYWORD)
            {
                opKeyword = (Keyword)_currentToken.Value!;

                if (opKeyword != Keyword.And && opKeyword != Keyword.Or)
                {
                    return res.Failure(ParserDiagnostics.InvalidOperatorOverload(_currentToken));
                }
            }
            else if (!IsOperatorToken(_currentToken.Type))
            {
                return res.Failure(new InvalidSyntaxError(
                    _currentToken.PositionStart,
                    _currentToken.PositionEnd,
                    $"expected an operator symbol but found {DescribeToken(_currentToken)}",
                    DiagnosticCode.ParserExpectedToken,
                    help: "overloadable operators are '+', '-', '*', '/', '==', '!=', '<', '>', '<=', '>=', '&', '|', '^', '<<', '>>', 'and' or 'or'",
                    primaryLabel: "operator symbol expected here"));
            }

            var operatorTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

            var argNameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? argType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                argType = ParseType(res);
                if (argType == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeName(_currentToken, after: "':'"));
            }
            else
            {
                return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                    "operator parameters require an explicit type annotation",
                    DiagnosticCode.ParserExpectedType,
                    help: "annotate each parameter with ': Type', e.g. 'operator+(rhs: Vec)'",
                    primaryLabel: "missing parameter type"));
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RPAREN)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '('));

            res.RegisterAdvancement();
            Advance();

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();
                returnType = ParseType(res);
                if (returnType == null)
                    return res.Failure(ParserDiagnostics.ExpectedReturnType(_currentToken));
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            AstNode? bodyNode = null;
            bool shouldAutoReturn = false;

            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();

                var scope = res.Register(ParseStatements());
                if (res.Error != null) return res;
                bodyNode = scope;

                res.RegisterAdvancement();
                Advance();
            }
            else if (_currentToken.Type == TokenType.ARROW)
            {
                shouldAutoReturn = true;
                res.RegisterAdvancement();
                Advance();

                var expr = res.Register(ParseStatement());
                if (res.Error != null) return res;
                bodyNode = expr;
            }
            else
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'{' (multi-statement body) or '->' (expression body)",
                    contextHint: "operator overloads need a body: '{ ... }' or '=> expr'"));
            }

            if (bodyNode == null)
                return res.Failure(ParserDiagnostics.ExpectedOperatorBody(_currentToken));

            return res.Success(new RaLanguage.Parser.Nodes.Classes.OperatorDefinitionNode(
                isPublic, isOverride, isStatic, operatorTok, argNameTok, argType, returnType, bodyNode, shouldAutoReturn, genericTypeParams, whereConstraints));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParseAsyncFunctionDefinition(bool isPublic = false)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Async))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "async", context: "to start an async function declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            bool isAsyncStream = false;
            if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
            {
                isAsyncStream = true;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            if (!_currentToken.Matches(Keyword.Fn))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn",
                    context: "after 'async' (or 'async stream')",
                    help: "async functions are declared 'async fn ...' or 'async stream fn ...'"));

            return ParseFunctionDefinition(isPublic: isPublic, isAsync: true, isAsyncStream: isAsyncStream);
        }


        private ParserResult ParseFunctionDefinition(string? ownerTypeName = null, bool isPublic = false, bool isOverride = false, bool isAbstract = false, bool isStatic = false, bool isDeclaringConstructor = false, bool isAsync = false, bool isAsyncStream = false)
        {
            var res = new ParserResult();

            if (!isDeclaringConstructor)
            {
                if (!_currentToken.Matches(Keyword.Fn))
                    return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "fn", context: "to begin a function declaration"));

                res.RegisterAdvancement();
                Advance();
            }
            else
            {
                if (_currentToken.Matches(Keyword.Fn))
                {
                    return res.Failure(new InvalidSyntaxError(_currentToken.PositionStart, _currentToken.PositionEnd,
                    "constructors must not be preceded by the 'fn' keyword",
                    DiagnosticCode.ParserInvalidSyntax,
                    help: "declare constructors using the type name directly, e.g. 'Point(x, y) { ... }'",
                    primaryLabel: "unexpected 'fn' before constructor"));
                }
            }

            Token? varNameTok = null;
            var genericTypeParams = new List<string>();
            List<CaptureSpec>? captureList = null;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.IDENTIFIER)
            {
                varNameTok = _currentToken;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                res.Register(ParseOptionalCaptureList(out captureList));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '('));
            }
            else if (_currentToken.Type == TokenType.LT)
            {
                res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                res.Register(ParseOptionalCaptureList(out captureList));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(', context: "the parameter list (after the generic type parameters)"));
            }
            else
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                res.Register(ParseOptionalCaptureList(out captureList));
                if (res.Error != null) return res;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.LPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a function name or '('",
                        contextHint: "function declarations need either a name (e.g. 'fn foo(...)') or '(' for an anonymous function"));
            }

            PushGenericScope(genericTypeParams);
            try
            {
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var argNameToks = new List<Token>();
            var argTypes = new List<TypeDescriptor?>();
            var isRefParams = new List<bool>();
            var paramDefaults = new List<AstNode?>();
            var paramAnnotations = new List<List<AnnotationApplicationNode>?>();
            List<AnnotationApplicationNode>? varArgAnnotations = null;
            bool hasVarArgs = false;
            Token? varArgNameTok = null;
            TypeDescriptor? varArgType = null;

            bool sawDefault = false;

            if (_currentToken.Type == TokenType.RPAREN)
            {
                goto otherRparen;
            }

            if (_currentToken.Type == TokenType.IDENTIFIER || _currentToken.Type == TokenType.SPREAD || _currentToken.Matches(Keyword.Ref) || _currentToken.Type == TokenType.AT_SIGN)
            {
                while (true)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    List<AnnotationApplicationNode>? pendingParamAnnotations = null;
                    if (_currentToken.Type == TokenType.AT_SIGN)
                    {
                        var (annList, annErr) = ParseAnnotationListInline(res);
                        if (annErr != null) return res.Failure(annErr);
                        pendingParamAnnotations = annList;
                    }

                    if (_currentToken.Type == TokenType.SPREAD)
                    {
                        hasVarArgs = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'...'", help: "variadic parameters take an identifier, e.g. '...args: int'"));

                        varArgNameTok = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedVarArgsType(_currentToken));

                            varArgType = parsed;
                        }

                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.VariadicMustBeLast(_currentToken.PositionStart, _currentToken.PositionEnd));

                        varArgAnnotations = pendingParamAnnotations;
                        break;
                    }
                    else
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        bool isRef = false;
                        if (_currentToken.Matches(Keyword.Ref))
                        {
                            isRef = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        var paramTok = _currentToken;
                        argNameToks.Add(paramTok);
                        paramAnnotations.Add(pendingParamAnnotations);
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        TypeDescriptor? ptype = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            var parsed = ParseType(res);
                            if (parsed == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));

                            if (isRef)
                            {
                                ptype = TypeDescriptor.RefType(parsed);
                            }
                            else
                            {
                                ptype = parsed;
                            }
                        }
                        argTypes.Add(ptype);
                        isRefParams.Add(isRef);

                        AstNode? defaultExpr = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            defaultExpr = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault)
                        {
                            return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        paramDefaults.Add(defaultExpr);

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "',' or ')'", contextHint: "the parameter / argument list is comma-separated and ends with ')'"));
            }
            else
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken, "a parameter name or ')'", contextHint: "parameter lists begin with names or '...' and end with ')'"));
            }

            otherRparen:  res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            TypeDescriptor? returnType = null;
            if (_currentToken.Type == TokenType.COLON)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var parsed = ParseType(res);
                if (parsed == null)
                    return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "the return type annotation"));

                returnType = parsed;
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            bool isConstructor = ownerTypeName != null
                                 && varNameTok != null
                                 && string.Equals(varNameTok.Value.ToString(), ownerTypeName, StringComparison.Ordinal);

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type == TokenType.ARROW)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                var body = res.Register(ParseExpression());
                if (res.Error != null) return res;

                return res.Success(new FunctionDefinitionNode(
                    varNameTok,
                    argNameToks,
                    argTypes,
                    isRefParams,
                    paramDefaults,
                    hasVarArgs,
                    varArgNameTok,
                    varArgType,
                    returnType,
                    body,
                    true,
                    genericTypeParams,
                    isPublic,
                    isConstructor,
                    isOverride,
                    isAbstract,
                    isStatic,
                    whereConstraints,
                    paramAnnotations,
                    captureList
                ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
            {
                return res.Success(new FunctionDefinitionNode(
                    varNameTok,
                    argNameToks,
                    argTypes,
                    isRefParams,
                    paramDefaults,
                    hasVarArgs,
                    varArgNameTok,
                    varArgType,
                    returnType,
                    null,
                    false,
                    genericTypeParams,
                    isPublic,
                    isConstructor,
                    isOverride,
                    isAbstract,
                    isStatic,
                    whereConstraints,
                    paramAnnotations,
                    captureList
                ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }

            res.RegisterAdvancement();
            Advance();

            var bodyStmts = res.Register(ParseStatements());
            if (res.Error != null) return res;

            if (_currentToken.Type != TokenType.RBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{'));

            res.RegisterAdvancement();
            Advance();

            return res.Success(new FunctionDefinitionNode(
                varNameTok,
                argNameToks,
                argTypes,
                isRefParams,
                paramDefaults,
                hasVarArgs,
                varArgNameTok,
                varArgType,
                returnType,
                bodyStmts,
                false,
                genericTypeParams,
                isPublic,
                isConstructor,
                isOverride,
                isAbstract,
                isStatic,
                whereConstraints,
                paramAnnotations,
                captureList
            ) { VarArgAnnotations = varArgAnnotations, IsAsync = isAsync, IsAsyncStream = isAsyncStream });
            }
            finally
            {
                PopGenericScope();
            }
        }


        // ============================================================
        // Records
        //
        //   [pub] record [class] Name [<T,U>] (f1[: T1] [= def], ...) [where ...] [{ methods/operators }]
        //
        // Primary fields are positional, public by default and immutable
        // (`let`-like). `pub` and `priv` modifiers may be used inline per
        // field, as well as `mut` to opt out of immutability. No
        // additional instance fields may appear in the optional body —
        // only methods and operator overloads.
        // ============================================================
        private ParserResult ParseRecordDefinition(bool isPublic, bool isAbstract)
        {
            var res = new ParserResult();
            // Consume `record`.
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Optional `class` — flips the record into reference flavour.
            bool isRefRecord = false;
            if (_currentToken.Matches(Keyword.Class))
            {
                isRefRecord = true;
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            if (isAbstract && !isRefRecord)
            {
                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "'class' after 'abstract record'",
                    contextHint: "only 'abstract record class' is permitted — value records are always sealed"));
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'record'",
                    help: "record declarations begin with a name, e.g. 'record Point(x: int, y: int)'"));

            var nameTok = _currentToken;
            var recordName = nameTok.Value?.ToString() ?? "";

            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LPAREN)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '(',
                    context: "the primary-constructor parameter list of a record"));

            res.RegisterAdvancement();
            Advance();

            var primaryFields = new List<RecordPrimaryFieldNode>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            while (_currentToken.Type != TokenType.RPAREN)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RPAREN) break;

                // Inline modifiers on a primary field.
                bool fieldPublic = true;
                bool fieldMutable = false;

                while (true)
                {
                    if (_currentToken.Matches(Keyword.Pub))
                    {
                        fieldPublic = true;
                        res.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    if (_currentToken.Matches(Keyword.Mut))
                    {
                        fieldMutable = true;
                        res.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    // Identifier "priv" used as a soft-modifier (not a
                    // dedicated keyword). Reserved usage to opt fields
                    // out of public visibility without bloating the
                    // keyword table.
                    if (_currentToken.Type == TokenType.IDENTIFIER &&
                        string.Equals(_currentToken.Value?.ToString(), "priv", StringComparison.Ordinal))
                    {
                        fieldPublic = false;
                        res.RegisterAdvancement();
                        Advance();
                        continue;
                    }

                    break;
                }

                if (_currentToken.Type != TokenType.IDENTIFIER)
                    return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken,
                        after: "the start of a record primary field",
                        help: "primary fields look like 'name: Type' or 'name: Type = default'"));

                var fieldNameTok = _currentToken;
                var fieldName = fieldNameTok.Value?.ToString() ?? "";

                if (!seenNames.Add(fieldName))
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(fieldNameTok,
                        $"a unique primary-field name (duplicate '{fieldName}')",
                        contextHint: "every primary field must have a distinct name; record auto-generated equality / hash / to_string consume the field list as-is"));
                }

                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? fieldType = null;
                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                            where: $"the type of primary field '{fieldName}'"));

                    fieldType = parsedType;
                }

                AstNode? defaultValueNode = null;
                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var defaultExpr = res.Register(ParseExpression());
                    if (res.Error != null) return res;
                    defaultValueNode = defaultExpr;
                }

                primaryFields.Add(new RecordPrimaryFieldNode(
                    fieldNameTok,
                    fieldType,
                    defaultValueNode,
                    fieldPublic,
                    fieldMutable));

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                    continue;
                }

                break;
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.RPAREN)
                return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(',
                    context: "the primary-constructor parameter list"));

            res.RegisterAdvancement();
            Advance();

            // Optional base specifier: `: Base(arg1, arg2, ...)` — only
            // allowed on `record class`. The base's primary fields are
            // PREPENDED to the child's at definition time so the merged
            // layout is observable through the same primary-field list
            // (equality, to_string, deconstruct all see the full set).
            // BaseArgs are parsed but currently informational; the
            // visitor enforces the inherited-fields-not-redeclared rule
            // and uses base PrimaryFields directly.
            TypeDescriptor? baseType = null;
            List<AstNode>? baseArgs = null;
            {
                int peekBase = _tokenIndex;
                while (peekBase < _tokens.Count && _tokens[peekBase].Type == TokenType.NEWLINE) peekBase++;
                if (peekBase < _tokens.Count && _tokens[peekBase].Type == TokenType.COLON)
                {
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    // Consume ':'.
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    var parsedBase = ParseType(res);
                    if (parsedBase == null)
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken,
                            where: "the base record after ':'"));
                    baseType = parsedBase;

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.LPAREN)
                    {
                        res.RegisterAdvancement();
                        Advance();
                        baseArgs = new List<AstNode>();
                        while (_currentToken.Type != TokenType.RPAREN)
                        {
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RPAREN) break;
                            var argExpr = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                            baseArgs.Add(argExpr!);
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.COMMA)
                            {
                                res.RegisterAdvancement();
                                Advance();
                                continue;
                            }
                            break;
                        }
                        if (_currentToken.Type != TokenType.RPAREN)
                            return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(',
                                context: "the base-record argument list"));
                        res.RegisterAdvancement();
                        Advance();
                    }
                }
            }

            // Optional `where` clause + optional body. Both are
            // newline-tolerant individually but we cannot let the
            // outer NEWLINE between `record X(...)` and the next
            // top-level statement get eaten if neither follow-on
            // exists — ParseStatements relies on that NEWLINE as the
            // statement terminator. So we peek through whitespace
            // first and only consume when we see `where` or `{`.
            List<WhereConstraintNode> whereConstraints = new List<WhereConstraintNode>();
            int peekWhere = _tokenIndex;
            while (peekWhere < _tokens.Count && _tokens[peekWhere].Type == TokenType.NEWLINE) peekWhere++;
            bool hasWhere = peekWhere < _tokens.Count
                            && _tokens[peekWhere].Type == TokenType.KEYWORD
                            && _tokens[peekWhere].Value is Keyword wkw
                            && wkw == Keyword.Where;
            if (hasWhere)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
                if (res.Error != null) return res;
            }

            var methods = new List<StructMethodDefinitionNode>();
            var operators = new List<OperatorDefinitionNode>();
            var recordProperties = new List<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode>();

            int peek = _tokenIndex;
            while (peek < _tokens.Count && _tokens[peek].Type == TokenType.NEWLINE) peek++;

            bool hasBody = peek < _tokens.Count && _tokens[peek].Type == TokenType.LBRACKET;

            if (!hasBody)
            {
                return res.Success(new RecordDefinitionNode(
                    nameTok,
                    isPublic,
                    isRefRecord,
                    isAbstract,
                    baseType,
                    baseArgs,
                    primaryFields,
                    methods,
                    operators,
                    genericTypeParams,
                    whereConstraints,
                    recordProperties));
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // Consume `{`.
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET) break;

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool memberPublic = true;
                if (_currentToken.Matches(Keyword.Pub))
                {
                    memberPublic = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }
                else if (_currentToken.Type == TokenType.IDENTIFIER &&
                         string.Equals(_currentToken.Value?.ToString(), "priv", StringComparison.Ordinal))
                {
                    memberPublic = false;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                // Property declarations on records — body-side. These
                // are body-only and do not participate in the primary
                // tuple, so structural equality / hash / to_string
                // continue to operate over the header. Computed and
                // observe-only properties are explicitly allowed;
                // stored properties on records are permitted but the
                // auto-derive equality remains anchored to the
                // primary-field list (see RA_PROPERTIES_DESIGN §4.14).
                bool recordMemberLazy = false;
                if (_currentToken.Matches(Keyword.Lazy))
                {
                    recordMemberLazy = true;
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Prop))
                {
                    var propRes = ParsePropertyDeclaration(
                        isPublic: memberPublic,
                        isStatic: false,
                        isAbstract: false,
                        isOverride: false,
                        isLazy: recordMemberLazy);
                    if (propRes.Error != null) return res.Failure(propRes.Error);
                    var propNode = (RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode)propRes.Node!;
                    AnnotationAttacher.Attach(propNode, memberAnnotations);
                    recordProperties.Add(propNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    continue;
                }

                if (recordMemberLazy)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'prop' (after 'lazy')",
                        contextHint: "'lazy' is only meaningful as a 'prop' prefix"));
                }

                // Records explicitly forbid extra instance fields in the
                // body — `var`/`let`/`const`/`final` would silently fall
                // outside the auto-generated equality/hash/to_string set.
                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Let) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final))
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "a method ('fn'), an operator overload, or '}'",
                        contextHint: "records cannot declare extra instance fields in the body — primary-constructor parameters are the single source of truth. Add the field to the header instead."));
                }

                bool memberIsAsync = false;
                bool memberIsAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    memberIsAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER &&
                        string.Equals(_currentToken.Value?.ToString(), "stream", StringComparison.Ordinal))
                    {
                        memberIsAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (_currentToken.Matches(Keyword.Fn))
                {
                    var fnRes = ParseFunctionDefinition(ownerTypeName: recordName, isPublic: memberPublic, isDeclaringConstructor: false, isAsync: memberIsAsync, isAsyncStream: memberIsAsyncStream);
                    if (fnRes.Error != null) return fnRes;

                    var methodNode = (FunctionDefinitionNode)fnRes.Node!;
                    AnnotationAttacher.Attach(methodNode, memberAnnotations);
                    methods.Add(new StructMethodDefinitionNodeFromFunctionDefinition(methodNode));

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Operator))
                {
                    var opRes = ParseOperatorDefinition(isPublic: memberPublic, ownerTypeName: null);
                    if (opRes.Error != null) return opRes;

                    var opNode = (OperatorDefinitionNode)opRes.Node!;
                    AnnotationAttacher.Attach(opNode, memberAnnotations);
                    operators.Add(opNode);

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a method ('fn'), an operator overload, or '}'",
                    contextHint: "record bodies allow methods and operator overloads only"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new RecordDefinitionNode(
                nameTok,
                isPublic,
                isRefRecord,
                isAbstract,
                baseType,
                baseArgs,
                primaryFields,
                methods,
                operators,
                genericTypeParams,
                whereConstraints,
                recordProperties));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParseStructDefinition(bool isPublic)
        {
            var res = new ParserResult();
            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken, after: "'struct'", help: "struct declarations begin with a name, e.g. 'struct Point { x: int, y: int }'"));

            var nameTok = _currentToken;
            var structName = nameTok.Value?.ToString() ?? "";

            res.RegisterAdvancement();
            Advance();

            List<string> genericTypeParams;
            res.Register(ParseOptionalGenericTypeParameters(out genericTypeParams));
            if (res.Error != null) return res;

            PushGenericScope(genericTypeParams);
            try
            {

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            List<WhereConstraintNode> whereConstraints;
            res.Register(ParseOptionalWhereClause(genericTypeParams, out whereConstraints));
            if (res.Error != null) return res;

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.LBRACKET)
                return res.Failure(ParserDiagnostics.ExpectedOpening(_currentToken, '{'));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            var fields = new List<StructFieldDefinitionNode>();
            var methods = new List<StructMethodDefinitionNode>();
            var operators = new List<OperatorDefinitionNode>();
            var properties = new List<RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode>();

            while (_currentToken.Type != TokenType.RBRACKET)
            {
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type == TokenType.RBRACKET)
                    break;

                List<AnnotationApplicationNode>? memberAnnotations = null;
                if (_currentToken.Type == TokenType.AT_SIGN)
                {
                    var (annList, annErr) = ParseAnnotationListInline(res);
                    if (annErr != null) return res.Failure(annErr);
                    memberAnnotations = annList;
                }

                bool memberPublic = false;

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Matches(Keyword.Pub))
                {
                    memberPublic = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Var) ||
                    _currentToken.Matches(Keyword.Const) ||
                    _currentToken.Matches(Keyword.Final) ||
                    _currentToken.Matches(Keyword.Let))
                {
                    var declRes = ParseVariableDeclaration(memberPublic);
                    if (declRes.Error != null) return declRes;

                    var declNode = (VariableDeclarationNode)declRes.Node!;
                    foreach (var d in declNode.Declarations)
                    {
                        var fieldNode = new StructFieldDefinitionNode(
                            declNode.IsPublic,
                            d.Item1,
                            d.Item3,
                            d.Item2,
                            false,
                            false,
                            false,
                            declNode.DeclarationType
                        );
                        AnnotationAttacher.Attach(fieldNode, memberAnnotations);
                        fields.Add(fieldNode);
                    }

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                // Property declaration. Structs do not allow override /
                // abstract / static on properties (no inheritance, no
                // static surface) — those modifier slots are 'false'.
                bool memberIsLazy = false;
                if (_currentToken.Matches(Keyword.Lazy))
                {
                    memberIsLazy = true;
                    res.RegisterAdvancement();
                    Advance();
                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                }

                if (_currentToken.Matches(Keyword.Prop))
                {
                    var propRes = ParsePropertyDeclaration(
                        isPublic: memberPublic,
                        isStatic: false,
                        isAbstract: false,
                        isOverride: false,
                        isLazy: memberIsLazy);
                    if (propRes.Error != null) return res.Failure(propRes.Error);
                    var propNode = (RaLanguage.Parser.Nodes.Properties.PropertyDefinitionNode)propRes.Node!;
                    AnnotationAttacher.Attach(propNode, memberAnnotations);
                    properties.Add(propNode);

                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }
                    continue;
                }

                if (memberIsLazy)
                {
                    return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                        "'prop' (after 'lazy')",
                        contextHint: "'lazy' is only meaningful as a 'prop' prefix"));
                }

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                bool memberIsAsync = false;
                bool memberIsAsyncStream = false;
                if (_currentToken.Matches(Keyword.Async))
                {
                    memberIsAsync = true;
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    if (_currentToken.Type == TokenType.IDENTIFIER && string.Equals(_currentToken.Value?.ToString(), "stream", System.StringComparison.Ordinal))
                    {
                        memberIsAsyncStream = true;
                        res.RegisterAdvancement();
                        Advance();

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }
                    }
                }

                if (_currentToken.Matches(Keyword.Fn) || (_currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName))
                {
                    var fnRes = ParseFunctionDefinition(ownerTypeName: structName, isPublic: memberPublic, isDeclaringConstructor: _currentToken.Type == TokenType.IDENTIFIER && _currentToken.Value.ToString() == structName, isAsync: memberIsAsync, isAsyncStream: memberIsAsyncStream);
                    if (fnRes.Error != null) return fnRes;

                    var methodNode = (FunctionDefinitionNode)fnRes.Node!;
                    AnnotationAttacher.Attach(methodNode, memberAnnotations);
                    methods.Add(new StructMethodDefinitionNodeFromFunctionDefinition(methodNode));
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                if (_currentToken.Matches(Keyword.Operator))
                {
                    var opRes = ParseOperatorDefinition(isPublic: memberPublic, ownerTypeName: null);
                    if (opRes.Error != null) return opRes;

                    var opNode = (OperatorDefinitionNode)opRes.Node!;
                    AnnotationAttacher.Attach(opNode, memberAnnotations);
                    operators.Add(opNode);
                    if (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    continue;
                }

                return res.Failure(ParserDiagnostics.UnexpectedToken(_currentToken,
                    "a field declaration ('var' / 'let' / 'const' / 'final'), a method ('fn') or an operator overload ('operator')",
                    contextHint: "class / struct bodies allow only fields, methods and operator overloads"));
            }

            res.RegisterAdvancement();
            Advance();

            return res.Success(new StructDefinitionNode(nameTok, isPublic, fields, methods, operators, genericTypeParams, whereConstraints, properties));
            }
            finally
            {
                PopGenericScope();
            }
        }


        private ParserResult ParseVariableDeclaration(bool isPublic = false, bool isStatic = false)
        {
            ParserResult res = new ParserResult();
            VariableDeclarationType variableDeclarationType = VariableDeclarationType.VARIABLE;

            if (_currentToken.Matches(Keyword.Const))
            {
                variableDeclarationType = VariableDeclarationType.CONST;
            }
            else if (_currentToken.Matches(Keyword.Final))
            {
                variableDeclarationType = VariableDeclarationType.FINAL;
            }
            else if (_currentToken.Matches(Keyword.Let))
            {
                variableDeclarationType = VariableDeclarationType.LET;
            }

            res.RegisterAdvancement();
            Advance();

            // `let` opens an extended-modifier grammar: `let mut x` and `let const x`.
            // These are siblings of `let`, not separate keywords, so they only attach
            // to LET (var/const/final keep their classic semantics untouched).
            if (variableDeclarationType == VariableDeclarationType.LET)
            {
                if (_currentToken.Matches(Keyword.Mut))
                {
                    variableDeclarationType = VariableDeclarationType.LET_MUT;
                    res.RegisterAdvancement();
                    Advance();
                }
                else if (_currentToken.Matches(Keyword.Const))
                {
                    variableDeclarationType = VariableDeclarationType.LET_CONST;
                    res.RegisterAdvancement();
                    Advance();
                }
            }

            List<(Token, AstNode?, TypeDescriptor?)> declarations = new List<(Token, AstNode?, TypeDescriptor?)>();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

            while (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var varName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? declaredType = null;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                    }
                    declaredType = parsedType;
                }

                AstNode? expr = null;

                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();
                    expr = res.Register(ParseExpression());

                    if (res.Error != null)
                    {
                        return res;
                    }
                }

                declarations.Add((varName, expr, declaredType));

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    break;
                }
            }

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, isStatic));
        }

        private ParserResult ParseInterfaceFieldDeclaration(bool isPublic = false)
        {
            ParserResult res = new ParserResult();
            VariableDeclarationType variableDeclarationType = VariableDeclarationType.VARIABLE;

            if (_currentToken.Matches(Keyword.Const))
            {
                variableDeclarationType = VariableDeclarationType.CONST;
            }
            else if (_currentToken.Matches(Keyword.Final))
            {
                variableDeclarationType = VariableDeclarationType.FINAL;
            }
            else if (_currentToken.Matches(Keyword.Let))
            {
                variableDeclarationType = VariableDeclarationType.LET;
            }

            res.RegisterAdvancement();
            Advance();

            List<(Token, AstNode?, TypeDescriptor?)> declarations = new List<(Token, AstNode?, TypeDescriptor?)>();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

            while (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var varName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? declaredType = null;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                    }
                    declaredType = parsedType;
                }

                if (_currentToken.Type == TokenType.EQ)
                {
                    return res.Failure(ParserDiagnostics.InterfaceFieldHasDefault(_currentToken.PositionStart, _currentToken.PositionEnd));
                }

                declarations.Add((varName, null, declaredType));

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    break;
                }
            }

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, false));
        }

        private ParserResult ParseTraitFieldDeclaration(bool isPublic = false)
        {
            ParserResult res = new ParserResult();
            VariableDeclarationType variableDeclarationType = VariableDeclarationType.VARIABLE;

            if (_currentToken.Matches(Keyword.Const))
            {
                variableDeclarationType = VariableDeclarationType.CONST;
            }
            else if (_currentToken.Matches(Keyword.Final))
            {
                variableDeclarationType = VariableDeclarationType.FINAL;
            }
            else if (_currentToken.Matches(Keyword.Let))
            {
                variableDeclarationType = VariableDeclarationType.LET;
            }

            res.RegisterAdvancement();
            Advance();

            List<(Token, AstNode?, TypeDescriptor?)> declarations = new List<(Token, AstNode?, TypeDescriptor?)>();

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedIdentifier(_currentToken));

            while (_currentToken.Type == TokenType.IDENTIFIER)
            {
                var varName = _currentToken;
                res.RegisterAdvancement();
                Advance();

                TypeDescriptor? declaredType = null;

                if (_currentToken.Type == TokenType.COLON)
                {
                    res.RegisterAdvancement();
                    Advance();

                    var parsedType = ParseType(res);
                    if (parsedType == null)
                    {
                        return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken));
                    }
                    declaredType = parsedType;
                }

                AstNode? defaultValueNode = null;
                if (_currentToken.Type == TokenType.EQ)
                {
                    res.RegisterAdvancement();
                    Advance();

                    while (_currentToken.Type == TokenType.NEWLINE)
                    {
                        res.RegisterAdvancement();
                        Advance();
                    }

                    defaultValueNode = res.Register(ParseExpression());
                    if (res.Error != null)
                    {
                        return res;
                    }
                }

                declarations.Add((varName, defaultValueNode, declaredType));

                if (_currentToken.Type == TokenType.COMMA)
                {
                    res.RegisterAdvancement();
                    Advance();
                }
                else
                {
                    break;
                }
            }

            return res.Success(new VariableDeclarationNode(variableDeclarationType, declarations, isPublic, false));
        }

        // Pre-allocated operator precedence tables. The previous implementation allocated a
        // List<(TokenType, Keyword?)> at every call to ParseBinaryOperation. With these static
        // arrays parsing a single expression no longer pays for ~6 short-lived list
        // allocations and the Any(lambda) closure inspections each can hide.

        private (List<AnnotationApplicationNode>? List, Errors.Error? Error) ParseAnnotationListInline(ParserResult outerRes)
        {
            if (_currentToken.Type != TokenType.AT_SIGN)
                return (null, null);

            var list = new List<AnnotationApplicationNode>();

            while (_currentToken.Type == TokenType.AT_SIGN)
            {
                var (node, err) = ParseSingleAnnotationApplication(outerRes);
                if (err != null) return (null, err);
                list.Add(node!);

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    outerRes.RegisterAdvancement();
                    Advance();
                }
            }

            return (list, null);
        }

        private (AnnotationApplicationNode? Node, Errors.Error? Error) ParseSingleAnnotationApplication(ParserResult outerRes)
        {
            if (_currentToken.Type != TokenType.AT_SIGN)
                return (null, ParserDiagnostics.ExpectedAtSign(_currentToken));

            var startPos = _currentToken.PositionStart;
            outerRes.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                outerRes.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return (null, ParserDiagnostics.ExpectedAnnotationName(_currentToken, after: "'@'"));

            var nameTok = _currentToken;
            Position endPos = nameTok.PositionEnd;
            outerRes.RegisterAdvancement();
            Advance();

            var positional = new List<AstNode>();
            var named = new List<(Token, AstNode)>();

            if (_currentToken.Type == TokenType.LPAREN)
            {
                outerRes.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    outerRes.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                        }

                        Token? namedKey = null;
                        if (_currentToken.Type == TokenType.IDENTIFIER &&
                            _tokenIndex + 1 < _tokens.Count &&
                            _tokens[_tokenIndex + 1].Type == TokenType.EQ)
                        {
                            namedKey = _currentToken;
                            outerRes.RegisterAdvancement();
                            Advance();
                            outerRes.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                outerRes.RegisterAdvancement();
                                Advance();
                            }
                        }

                        var exprRes = ParseExpression();
                        var exprNode = outerRes.Register(exprRes);
                        if (outerRes.Error != null) return (null, outerRes.Error);

                        if (namedKey != null) named.Add((namedKey.Value, exprNode));
                        else positional.Add(exprNode);

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            outerRes.RegisterAdvancement();
                            Advance();
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                outerRes.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return (null, ParserDiagnostics.UnexpectedToken(_currentToken,
                        "',' or ')'",
                        contextHint: "annotation argument lists are comma-separated and end with ')'"));

                endPos = _currentToken.PositionEnd;
                outerRes.RegisterAdvancement();
                Advance();
            }

            return (new AnnotationApplicationNode(nameTok, positional, named, startPos, endPos), null);
        }

        private ParserResult ParseAnnotationDefinition(bool isPublic)
        {
            var res = new ParserResult();

            if (!_currentToken.Matches(Keyword.Annotation))
                return res.Failure(ParserDiagnostics.ExpectedKeyword(_currentToken, "annotation", context: "to start an annotation declaration"));

            res.RegisterAdvancement();
            Advance();

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            if (_currentToken.Type != TokenType.IDENTIFIER)
                return res.Failure(ParserDiagnostics.ExpectedAnnotationName(_currentToken, after: "'annotation'"));

            var nameTok = _currentToken;
            res.RegisterAdvancement();
            Advance();

            var parameters = new List<AnnotationParameterNode>();

            if (_currentToken.Type == TokenType.LPAREN)
            {
                res.RegisterAdvancement();
                Advance();

                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RPAREN)
                {
                    bool sawDefault = false;
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        bool isVarArgs = false;
                        if (_currentToken.Type == TokenType.SPREAD)
                        {
                            isVarArgs = true;
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        var paramName = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        TypeDescriptor? paramType = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            paramType = ParseType(res);
                            if (paramType == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "an annotation parameter"));
                        }

                        AstNode? defaultValue = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            defaultValue = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault && !isVarArgs)
                        {
                            return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        parameters.Add(new AnnotationParameterNode(paramName, paramType, defaultValue, isVarArgs));

                        if (isVarArgs) break;

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RPAREN) break;
                            continue;
                        }

                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RPAREN)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, ')', '(', context: "the annotation parameter list"));

                res.RegisterAdvancement();
                Advance();
            }

            while (_currentToken.Type == TokenType.NEWLINE)
            {
                res.RegisterAdvancement();
                Advance();
            }

            // `annotation Name { p1: T1, p2: T2 = default }` body form.
            // Same parameter grammar as the parenthesised form, just delimited
            // by braces and accepting newline-separated entries as well as
            // commas.
            if (_currentToken.Type == TokenType.LBRACKET)
            {
                res.RegisterAdvancement();
                Advance();
                while (_currentToken.Type == TokenType.NEWLINE)
                {
                    res.RegisterAdvancement();
                    Advance();
                }

                if (_currentToken.Type != TokenType.RBRACKET)
                {
                    bool sawDefault = false;
                    while (true)
                    {
                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        bool isVarArgs = false;
                        if (_currentToken.Type == TokenType.SPREAD)
                        {
                            isVarArgs = true;
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type != TokenType.IDENTIFIER)
                            return res.Failure(ParserDiagnostics.ExpectedParameterName(_currentToken));

                        var paramName = _currentToken;
                        res.RegisterAdvancement();
                        Advance();

                        TypeDescriptor? paramType = null;
                        if (_currentToken.Type == TokenType.COLON)
                        {
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            paramType = ParseType(res);
                            if (paramType == null)
                                return res.Failure(ParserDiagnostics.ExpectedTypeAfterColon(_currentToken, where: "an annotation parameter"));
                        }

                        AstNode? defaultValue = null;
                        if (_currentToken.Type == TokenType.EQ)
                        {
                            sawDefault = true;
                            res.RegisterAdvancement();
                            Advance();

                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }

                            defaultValue = res.Register(ParseExpression());
                            if (res.Error != null) return res;
                        }
                        else if (sawDefault && !isVarArgs)
                        {
                            return res.Failure(ParserDiagnostics.DefaultParameterMustBeTrailing(_currentToken.PositionStart, _currentToken.PositionEnd));
                        }

                        parameters.Add(new AnnotationParameterNode(paramName, paramType, defaultValue, isVarArgs));

                        if (isVarArgs) break;

                        while (_currentToken.Type == TokenType.NEWLINE)
                        {
                            res.RegisterAdvancement();
                            Advance();
                        }

                        if (_currentToken.Type == TokenType.COMMA)
                        {
                            res.RegisterAdvancement();
                            Advance();
                            while (_currentToken.Type == TokenType.NEWLINE)
                            {
                                res.RegisterAdvancement();
                                Advance();
                            }
                            if (_currentToken.Type == TokenType.RBRACKET) break;
                            continue;
                        }

                        if (_currentToken.Type == TokenType.RBRACKET) break;
                        break;
                    }
                }

                if (_currentToken.Type != TokenType.RBRACKET)
                    return res.Failure(ParserDiagnostics.ExpectedClosing(_currentToken, '}', '{', context: "the annotation parameter list"));

                res.RegisterAdvancement();
                Advance();
            }

            var defNode = new AnnotationDefinitionNode(nameTok, isPublic, parameters);
            defNode.PositionEnd = _currentToken.PositionEnd;
            return res.Success(defNode);
        }

    }
}
