import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AdminFeaturedPage } from "@/pages/AdminFeaturedPage";
import { createQueryClient } from "@/shared/api/query-client";

describe("AdminFeaturedPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("reorders with keyboard-operable controls and saves the expected version", async () => {
    const calls: Array<{ comicIds:string[]; expectedVersion:number }> = [];
    vi.stubGlobal("fetch", vi.fn((input:string|URL|Request, init?:RequestInit) => {
      const url=input instanceof Request?input.url:input.toString();
      if(url.includes("/api/v1/admin/comics?")) return Promise.resolve(new Response(JSON.stringify({
        items:[{id:"a",title:"A",publicationStatus:"Published"},{id:"b",title:"B",publicationStatus:"Published"}],
        page:1,pageSize:30,totalItems:2,totalPages:1,
      }),{status:200,headers:{"Content-Type":"application/json"}}));
      if(url.endsWith("/api/v1/admin/featured") && (init?.method??"GET")==="GET") return Promise.resolve(new Response(JSON.stringify({version:3,comicIds:["a","b"]}),{status:200,headers:{"Content-Type":"application/json"}}));
      if(url.endsWith("/api/v1/admin/featured") && init?.method==="PUT") {const parsed:unknown=JSON.parse(init.body as string);if(typeof parsed!=="object"||parsed===null||!("comicIds" in parsed)||!("expectedVersion" in parsed))throw new Error("Invalid request");calls.push(parsed as {comicIds:string[];expectedVersion:number});return Promise.resolve(new Response(JSON.stringify({version:4,comicIds:["b","a"]}),{status:200,headers:{"Content-Type":"application/json"}}));}
      throw new Error(`Unexpected ${url}`);
    }));
    const user=userEvent.setup();
    render(<QueryClientProvider client={createQueryClient()}><AdminFeaturedPage/></QueryClientProvider>);
    await screen.findByText("A");
    await user.tab();
    await user.click(screen.getByRole("button",{name:"Đưa mục 2 lên"}));
    expect(screen.getAllByRole("listitem")[0]).toHaveTextContent("B");
    await user.click(screen.getByRole("button",{name:"Lưu thứ tự"}));
    expect(calls).toEqual([{comicIds:["b","a"],expectedVersion:3}]);
  });
});
