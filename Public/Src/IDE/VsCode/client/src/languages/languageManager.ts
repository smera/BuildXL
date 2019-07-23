import { LanguageCpp } from "./LanguageCpp";
import { LanguageClient } from "vscode-languageclient";

export class LanguageManager
{
    private cpp : LanguageCpp = undefined;

    async activate(languageClient : LanguageClient) : Promise<void>
    {
        this.cpp = new LanguageCpp();
        await this.cpp.activate(languageClient);
    }

    public deactivate() : void
    {
        if (this.cpp) {
            this.cpp.deactivate();
        }
    }
}