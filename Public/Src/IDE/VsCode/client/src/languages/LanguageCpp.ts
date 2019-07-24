import {
    getCppToolsApi,
    CppToolsApi,
    Version,
    CustomConfigurationProvider,
    SourceFileConfigurationItem,
    WorkspaceBrowseConfiguration
} from 'vscode-cpptools';
import { CancellationToken, RequestType } from 'vscode-jsonrpc';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient';
import { DebugSession } from 'vscode-debugadapter';
import { debug } from 'util';

/**
 *  JSON-RPC requests
 */
const CanProvideConfigurationRequest = new RequestType<CanProvideConfigurationParam, CanProvideConfigurationResult, any, any>("dscript-cpp/canProvideConfiguration");
const ProvideConfigurationsRequest = new RequestType<ProvideConfigurationsParam, ProvideConfigurationsResult, any, any>("dscript-cpp/provideConfiguration");

export class LanguageCpp implements CustomConfigurationProvider {

    
    private api: CppToolsApi | undefined;
    private languageClient: LanguageClient;
    // Tracks our disposable objects
    private disposables: vscode.Disposable[] = [];

    /**
     * @inheritDoc
     */
    public readonly name: string = "BuildXL DScript";

    /**
     * @inheritDoc
     */
    public readonly extensionId: string = "buildxldscript";

    public async activate(languageClient: LanguageClient): Promise<void> {

        let api = await getCppToolsApi(Version.v2);
        if (api) {
            // Inform cpptools that a custom config provider will be able to service the current workspace.
            api.registerCustomConfigurationProvider(this);

            if (api.notifyReady) {
                // Notify cpptools that the provider is ready to provide IntelliSense configurations.
                api.notifyReady(this);
            } else {
                // Running on a version of cpptools that doesn't support v2 yet.
                api.didChangeCustomConfiguration(this);
            }
        }

        this.disposables.push(api);

        this.api = api;
        this.languageClient = languageClient;
    }

    public deactivate(): void {
        if (this.api) {
            this.api.dispose();
        }
    }

    /**
    * A request to determine whether this provider can provide IntelliSense configurations for the given document.
    * @param uri The URI of the document.
    * @param token (optional) The cancellation token.
    * @returns 'true' if this provider can provide IntelliSense configurations for the given document.
    */
    public async canProvideConfiguration(uri: vscode.Uri, token?: CancellationToken): Promise<boolean> {
        var result = await this.languageClient.sendRequest(CanProvideConfigurationRequest, {uri: uri.fsPath}, token);
        return result.canProvideConfiguration;
    }

    /**
     * A request to get Intellisense configurations for the given files.
     * @param uris A list of one of more URIs for the files to provide configurations for.
     * @param token (optional) The cancellation token.
     * @returns A list of [SourceFileConfigurationItem](#SourceFileConfigurationItem) for the documents that this provider
     * is able to provide IntelliSense configurations for.
     * Note: If this provider cannot provide configurations for any of the files in `uris`, the provider may omit the
     * configuration for that file in the return value. An empty array may be returned if the provider cannot provide
     * configurations for any of the files requested.
     */
    public async provideConfigurations(uris: vscode.Uri[], token?: CancellationToken): Promise<SourceFileConfigurationItem[]> {
        var args : ProvideConfigurationsParam = {
            uris: []
        };
        for (var uri of uris)
        {
            args.uris.push(uri.fsPath);
        }

        var result = await this.languageClient.sendRequest(ProvideConfigurationsRequest, args, token);
        var sourceFileConfigurations : SourceFileConfigurationItem[] = [];

        // Update the paths to vscode uri's
        for (var sourceFileConfiguration of result.sourceFileConfigurations)
        {
            if (typeof sourceFileConfiguration.uri == "string")
            {
                sourceFileConfigurations.push({
                    uri: vscode.Uri.file(sourceFileConfiguration.uri),
                    configuration: sourceFileConfiguration.configuration
                });
            }
            else
            {
                sourceFileConfigurations.push(sourceFileConfiguration);
            }
        }

        return sourceFileConfigurations;
    }

    /**
     * A request to determine whether this provider can provide a code browsing configuration for the workspace folder.
     * @param token (optional) The cancellation token.
     * @returns 'true' if this provider can provider a code browsing configuration for the workspace folder.
     */
    public async canProvideBrowseConfiguration(token?: CancellationToken): Promise<boolean> {
        return false;
    }

    /**
     * A request to get the code browsing configuration for the workspace folder.
     * @returns A [WorkspaceBrowseConfiguration](#WorkspaceBrowseConfiguration) with the information required to
     * construct the equivalent of `browse.path` from `c_cpp_properties.json`.
     */
    public async provideBrowseConfiguration(token?: CancellationToken): Promise<WorkspaceBrowseConfiguration> {
        return undefined;
    }

    public dispose(): void {
        vscode.Disposable.from(...this.disposables).dispose();
    }
}

interface CanProvideConfigurationParam {
    uri: string,
};

interface CanProvideConfigurationResult {
    canProvideConfiguration: boolean,
};

interface ProvideConfigurationsParam {
    uris: string[],
};

interface ProvideConfigurationsResult {
    sourceFileConfigurations?: SourceFileConfigurationItem[];
};